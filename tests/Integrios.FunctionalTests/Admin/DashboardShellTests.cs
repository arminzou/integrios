using System.Net;
using System.Text.Json;
using Integrios.Admin.Auth;
using Integrios.Admin.Dashboard;
using Integrios.Tests.Shared;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Integrios.FunctionalTests.Admin;

public sealed class DashboardShellTests(AdminApiFixture fixture)
    : AdminApiTestBase, IClassFixture<AdminApiFixture>, IAsyncLifetime
{
    private HttpClient client = null!;

    public async Task InitializeAsync()
    {
        await fixture.ResetAsync();
        client = fixture.WebFactory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
    }

    public Task DisposeAsync()
    {
        client.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task UnknownApiAndOperationalRoutes_NeverAnswerWithTheDashboardShell()
    {
        // These must keep their real responses whether or not the dashboard is built, so a broken
        // client or a removed endpoint cannot be mistaken for a working page.
        foreach (string path in new[]
                 {
                     "/admin/not-a-capability",
                     "/admin/tenants/not-a-guid/sources",
                     "/auth/not-an-auth-route",
                 })
        {
            using HttpResponseMessage response = await client.SendAsync(AdminRequest(HttpMethod.Get, path));
            response.IsSuccessStatusCode.ShouldBeFalse(path);
            (response.Content.Headers.ContentType?.MediaType ?? "")
                .ShouldNotBe("text/html", $"{path} answered with the dashboard shell.");
        }

        // The OpenAPI document is a real response on this host; the point is that the fallback
        // never replaces it with the shell.
        using HttpResponseMessage openApi = await client.SendAsync(AdminRequest(HttpMethod.Get, "/openapi/v1.json"));
        openApi.Content.Headers.ContentType?.MediaType.ShouldNotBe("text/html");
    }

    [Fact]
    public async Task UnauthenticatedApiRequests_StayUnauthorizedRatherThanBecomingThePage()
    {
        using HttpResponseMessage response = await client.GetAsync("/admin/tenants");
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (response.Content.Headers.ContentType?.MediaType ?? "").ShouldNotBe("text/html");
    }

    [Fact]
    public async Task BrowserRoutes_ServeTheBuiltShellWhetherOrNotAHumanMethodIsConfigured()
    {
        string webRoot = Path.Combine(Path.GetTempPath(), "integrios-dashboard-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(webRoot);
        await File.WriteAllTextAsync(Path.Combine(webRoot, DashboardHosting.ShellFile), "<html>dashboard</html>");

        try
        {
            // A deployment with no human method still serves the shell and answers both browser
            // reads, so it reports "no sign-in method is configured" rather than a bare 404 or a
            // dead API.
            using WebApplicationFactory<Program> withoutOidc = fixture.WebFactory.WithWebHostBuilder(
                builder => builder.UseWebRoot(webRoot));
            using HttpClient unavailable = withoutOidc.CreateClient();
            using HttpResponseMessage unavailableRoot = await unavailable.GetAsync("/");
            unavailableRoot.StatusCode.ShouldBe(HttpStatusCode.OK);
            unavailableRoot.Content.Headers.ContentType?.MediaType.ShouldBe("text/html");

            using HttpResponseMessage unavailableSession = await unavailable.GetAsync(
                OperatorSessionEndpoints.BootstrapPath);
            unavailableSession.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            using HttpResponseMessage unavailableOptions = await unavailable.GetAsync(
                OperatorSessionEndpoints.OptionsPath);
            unavailableOptions.StatusCode.ShouldBe(HttpStatusCode.OK);
            JsonElement options = JsonDocument.Parse(
                await unavailableOptions.Content.ReadAsStringAsync()).RootElement;
            options.GetProperty("oidc_enabled").GetBoolean().ShouldBeFalse();
            options.GetProperty("password_enabled").GetBoolean().ShouldBeFalse();

            using WebApplicationFactory<Program> withOidc = fixture.WebFactory.WithWebHostBuilder(builder =>
            {
                builder.UseWebRoot(webRoot);
                builder.UseSetting(OperatorOidcOptions.AuthorityKey, "https://oidc.example.test");
                builder.UseSetting(OperatorOidcOptions.SectionKey + ":ClientId", "dashboard-test");
            });
            using HttpClient available = withOidc.CreateClient();
            foreach (string path in new[] { "/", "/tenants/overview" })
            {
                using HttpResponseMessage response = await available.GetAsync(path);
                response.StatusCode.ShouldBe(HttpStatusCode.OK);
                response.Content.Headers.ContentType?.MediaType.ShouldBe("text/html");
            }


            using WebApplicationFactory<Program> withPassword = fixture.WebFactory.WithWebHostBuilder(builder =>
            {
                builder.UseWebRoot(webRoot);
                builder.UseSetting(OperatorPasswordOptions.EnabledKey, "true");
            });
            using HttpClient passwordAvailable = withPassword.CreateClient();
            using HttpResponseMessage passwordRoot = await passwordAvailable.GetAsync("/");
            passwordRoot.StatusCode.ShouldBe(HttpStatusCode.OK);
            passwordRoot.Content.Headers.ContentType?.MediaType.ShouldBe("text/html");
            using HttpResponseMessage passwordOptions = await passwordAvailable.GetAsync(
                OperatorSessionEndpoints.OptionsPath);
            passwordOptions.StatusCode.ShouldBe(HttpStatusCode.OK);
        }
        finally
        {
            Directory.Delete(webRoot, recursive: true);
        }
    }
}
