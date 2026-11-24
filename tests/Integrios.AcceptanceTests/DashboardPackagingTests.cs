using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Integrios.AcceptanceTests;

[Collection(PackagedDeploymentCollection.Name)]
public sealed class DashboardPackagingTests(PackagedDeploymentFixture fixture)
{
    [Fact]
    public async Task AdminRole_ServesTheDashboardShellAndItsAssets()
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

    [Fact]
    public async Task AuthenticationOptions_DefaultToPasswordWithoutAnIdentityProvider()
    {
        using HttpResponseMessage response = await fixture.AdminClient.GetAsync("/auth/options");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement options = document.RootElement;
        options.GetProperty("oidc_enabled").GetBoolean().ShouldBeFalse();
        options.GetProperty("password_enabled").GetBoolean().ShouldBeTrue();
        options.GetProperty("antiforgery_token").GetString().ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task OnlyTheAdminRoleServesTheDashboardTheImageCarries()
    {
        IReadOnlyList<string> assets = await fixture.ListImageEntriesAsync(fixture.Image, "/app/wwwroot");
        assets.ShouldContain(DashboardShellFile);

        foreach ((string role, HttpClient client) in (ValueTuple<string, HttpClient>[])[
            ("ingestion", fixture.IngestionClient),
            ("worker", fixture.WorkerOperationalClient)])
        {
            using HttpResponseMessage response = await client.GetAsync("/");
            string body = await response.Content.ReadAsStringAsync();
            (response.IsSuccessStatusCode && body.Contains("<html", StringComparison.OrdinalIgnoreCase))
                .ShouldBeFalse($"The {role} role served the dashboard shell: {(int)response.StatusCode}");
        }
    }

    [Fact]
    public async Task TheImageCarriesNoDashboardBuildToolchain()
    {
        // The dashboard is built in a stage that is discarded. A runtime image carrying Node would
        // mean the build toolchain shipped with the service.
        (await fixture.ImageHasExecutableAsync(fixture.Image, "node")).ShouldBeFalse("The image contains node.");
        (await fixture.ImageHasExecutableAsync(fixture.Image, "npm")).ShouldBeFalse("The image contains npm.");
    }

    /// The header as sent, or a stand-in when it is absent. Read as text on purpose: reaching into
    /// the parsed CacheControl object through a null-conditional silently skips the assertion in the
    /// one case that matters, when no policy was sent at all.
    private static string CacheControlOf(HttpResponseMessage response) =>
        response.Headers.CacheControl?.ToString() ?? "(no Cache-Control header)";

    private const string DashboardShellFile = "index.html";
}
