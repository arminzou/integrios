using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Integrios.AcceptanceTests;

/// What the packaged Admin image actually serves and carries. Everything here runs against the
/// built images and the running deployment, not against a test host: the point is that the artifact
/// a deployment pulls behaves this way, not that the source could.
[Collection(PackagedDeploymentCollection.Name)]
public sealed class DashboardPackagingTests(PackagedDeploymentFixture fixture)
{
    [Fact]
    public async Task AdminImage_ServesTheDashboardShellAndItsAssets()
    {
        using HttpResponseMessage shell = await fixture.AdminClient.GetAsync("/");
        shell.StatusCode.ShouldBe(HttpStatusCode.OK);
        shell.Content.Headers.ContentType?.MediaType.ShouldBe("text/html");

        string html = await shell.Content.ReadAsStringAsync();
        // The shell references the hashed bundle the image was built with. Following that reference
        // is what proves the assets are present and served, rather than only the entry document.
        Match asset = Regex.Match(html, @"src=""(?<path>/assets/[^""]+\.js)""");
        asset.Success.ShouldBeTrue($"The served shell referenced no bundle: {html}");

        using HttpResponseMessage bundle = await fixture.AdminClient.GetAsync(asset.Groups["path"].Value);
        bundle.StatusCode.ShouldBe(HttpStatusCode.OK);
        bundle.Content.Headers.ContentType?.MediaType.ShouldBe("text/javascript");
        (await bundle.Content.ReadAsStringAsync()).ShouldNotBeEmpty();
    }

    [Fact]
    public async Task TheShellIsAlwaysRevalidatedAndItsHashedBundleNeverIs()
    {
        using HttpResponseMessage shell = await fixture.AdminClient.GetAsync("/");
        // Without this the browser is free to guess a freshness window from the file's age and skip
        // asking. An upgraded deployment would then keep serving a shell that names a bundle hash
        // it no longer has, and the dashboard would not render at all.
        CacheControlOf(shell).ShouldBe("no-cache", "The shell must be revalidated on every load.");

        // A browser route reaches the same shell through the fallback and must carry the same rule.
        using HttpResponseMessage deepLink = await fixture.AdminClient.GetAsync("/tenants");
        CacheControlOf(deepLink).ShouldBe("no-cache", "A browser route must be revalidated too.");

        string html = await shell.Content.ReadAsStringAsync();
        string bundle = Regex.Match(html, @"src=""(?<path>/assets/[^""]+\.js)""").Groups["path"].Value;
        bundle.ShouldNotBeEmpty();

        using HttpResponseMessage asset = await fixture.AdminClient.GetAsync(bundle);
        // The bundle's own name carries its content hash, so this URL can never mean anything else.
        CacheControlOf(asset).ShouldBe(
            "public, max-age=31536000, immutable",
            "A content-hashed bundle should never need revalidating.");
    }

    [Theory]
    [InlineData("/tenants")]
    [InlineData("/connectors")]
    [InlineData("/tenants/8b1d0f2e-0000-0000-0000-000000000000/events")]
    public async Task BrowserRoutes_FallBackToTheShellSoADeepLinkResolves(string path)
    {
        using HttpResponseMessage response = await fixture.AdminClient.GetAsync(path);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("text/html");
    }

    [Theory]
    [InlineData("/admin/not-a-capability")]
    [InlineData("/auth/not-an-endpoint")]
    [InlineData("/openapi/not-a-document")]
    public async Task ExcludedRoutes_KeepTheirOwnResponseInsteadOfTheShell(string path)
    {
        // Answering these with the dashboard would turn a broken client or a missing endpoint into a
        // page that looks like it worked.
        using HttpResponseMessage response = await fixture.AdminClient.GetAsync(path);
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType?.MediaType.ShouldNotBe("text/html");
    }

    [Fact]
    public async Task BrowserSessionBootstrap_RefusesAnAnonymousCaller()
    {
        using HttpResponseMessage session = await fixture.AdminClient.GetAsync("/auth/session");
        session.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        // An unauthenticated API call answers 401 rather than redirecting an XHR to the provider,
        // even though a browser sign-in path is configured in this deployment.
        using HttpResponseMessage api = await fixture.AdminClient.GetAsync("/admin/tenants");
        api.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task OperatorKeyAutomation_StaysUsableWithTheBrowserSurfaceConfigured()
    {
        // Antiforgery validates unsafe requests carrying the browser session cookie. A machine
        // credential is sent deliberately on every request and is never attached by a browser, so
        // enabling the browser surface must not start rejecting automation.
        string slug = $"packaged-dashboard-{Guid.NewGuid():N}"[..32];
        using var content = new StringContent(
            $$"""{"slug":"{{slug}}","name":"Packaged dashboard","environment":null,"description":null}""",
            Encoding.UTF8,
            "application/json");
        using var create = new HttpRequestMessage(HttpMethod.Post, "/admin/tenants") { Content = content };
        create.Headers.TryAddWithoutValidation("Authorization", fixture.AdminAuthorization);

        using HttpResponseMessage created = await fixture.AdminClient.SendAsync(create);
        created.StatusCode.ShouldBe(HttpStatusCode.Created);

        using var read = new HttpRequestMessage(HttpMethod.Get, "/admin/tenants?limit=20");
        read.Headers.TryAddWithoutValidation("Authorization", fixture.AdminAuthorization);
        using HttpResponseMessage list = await fixture.AdminClient.SendAsync(read);
        list.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await list.Content.ReadAsStringAsync()).ShouldContain(slug);
    }

    [Fact]
    public async Task OnlyTheAdminImageCarriesTheDashboard()
    {
        IReadOnlyList<string> adminAssets = await fixture.ListImageEntriesAsync(fixture.AdminImage, "/app/wwwroot");
        adminAssets.ShouldContain(DashboardShellFile);

        foreach (string image in (string[])[fixture.IngestionImage, fixture.WorkerImage, fixture.BootstrapImage])
        {
            IReadOnlyList<string> entries = await fixture.ListImageEntriesAsync(image, "/app/wwwroot");
            entries.ShouldNotContain(DashboardShellFile, $"{image} carries a dashboard shell.");
            entries.ShouldBeEmpty($"{image} carries dashboard assets: {string.Join(", ", entries)}");
        }
    }

    [Fact]
    public async Task NoImageCarriesTheDashboardBuildToolchain()
    {
        // The dashboard is built in a stage that is discarded. A runtime image carrying Node would
        // mean the build toolchain shipped with the service.
        foreach (string image in (string[])[
            fixture.AdminImage,
            fixture.BootstrapImage,
            fixture.IngestionImage,
            fixture.WorkerImage])
        {
            (await fixture.ImageHasExecutableAsync(image, "node")).ShouldBeFalse($"{image} contains node.");
            (await fixture.ImageHasExecutableAsync(image, "npm")).ShouldBeFalse($"{image} contains npm.");
        }
    }

    /// The header as sent, or a stand-in when it is absent. Read as text on purpose: reaching into
    /// the parsed CacheControl object through a null-conditional silently skips the assertion in the
    /// one case that matters, when no policy was sent at all.
    private static string CacheControlOf(HttpResponseMessage response) =>
        response.Headers.CacheControl?.ToString() ?? "(no Cache-Control header)";

    /// The bootstrap and migrate services run Admin command-line verbs from an image built without
    /// the dashboard. That they completed is asserted by the fixture's own startup gate; this
    /// records why the image they use is deliberately a different one.
    private const string DashboardShellFile = "index.html";
}
