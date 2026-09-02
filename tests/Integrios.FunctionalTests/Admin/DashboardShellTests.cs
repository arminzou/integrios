using System.Net;
using Integrios.Tests.Shared;
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
    public async Task BrowserRoutes_ServeTheShellWhenTheDashboardIsBuilt()
    {
        using HttpResponseMessage root = await client.GetAsync("/");
        if (root.StatusCode == HttpStatusCode.NotFound)
            // The frontend build has not run in this working tree; the packaged Acceptance leg is
            // what proves the shipped image contains the assets.
            return;

        root.StatusCode.ShouldBe(HttpStatusCode.OK);
        root.Content.Headers.ContentType?.MediaType.ShouldBe("text/html");

        // A deep browser route is the dashboard's own routing surface, not a missing page.
        using HttpResponseMessage deep = await client.GetAsync("/tenants/overview");
        deep.StatusCode.ShouldBe(HttpStatusCode.OK);
        deep.Content.Headers.ContentType?.MediaType.ShouldBe("text/html");
    }
}
