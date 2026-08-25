using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Integrios.Application.Authoring.TenantApiKeys;
using Microsoft.AspNetCore.Mvc.Testing;
using Integrios.Tests.Shared;

namespace Integrios.FunctionalTests.Admin;

public sealed class TenantApiKeysAdminTests : AdminApiTestBase, IClassFixture<AdminApiFixture>, IAsyncLifetime
{

    private readonly AdminApiFixture fixture;
    private HttpClient client = null!;

    public TenantApiKeysAdminTests(AdminApiFixture fixture)
    {
        this.fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        await fixture.ResetAsync();
        client = fixture.WebFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
    }

    public Task DisposeAsync()
    {
        client.Dispose();
        return Task.CompletedTask;
    }

    // Create

    [Fact]
    public async Task CreateTenantApiKey_ReturnsCreated_WithTokenAndKeyPrefix()
    {
        var response = await PostTenantApiKeyAsync("ingest-key");

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        response.Headers.Location.ShouldNotBeNull();

        var body = await response.Content.ReadFromJsonAsync<CreateTenantApiKeyResult>(HostJson.Options);
        body.ShouldNotBeNull();
        body.TenantApiKey.Name.ShouldBe("ingest-key");
        body.TenantApiKey.TenantId.ShouldBe(fixture.TenantId);
        body.TenantApiKey.Status.ShouldBe("active");

        // Token format: intg_<64hex>
        body.Token.ShouldStartWith("intg_", Case.Sensitive);
        body.Token.Length.ShouldBe(69); // "intg_" (5) + 64 hex chars

        // KeyPrefix is the display hint: first 12 chars of the token
        body.TenantApiKey.KeyPrefix.ShouldBe(body.Token[..12]);
    }

    [Fact]
    public async Task CreateTenantApiKey_ResponseDoesNotExposeScopes()
    {
        var response = await PostTenantApiKeyAsync("authority-contract-key");
        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("tenant_api_key").TryGetProperty("scopes", out _).ShouldBeFalse();
    }

    // Get

    [Fact]
    public async Task GetTenantApiKey_ReturnsKey_WithKeyPrefix()
    {
        var created = await CreateTenantApiKeyAsync("get-test-key");

        var response = await client.SendAsync(AdminRequest(
            HttpMethod.Get,
            $"/admin/tenants/{fixture.TenantId}/tenant-api-keys/{created.TenantApiKey.Id}"));
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<TenantApiKeyDto>(HostJson.Options);
        body.ShouldNotBeNull();
        body.Id.ShouldBe(created.TenantApiKey.Id);
        body.Name.ShouldBe("get-test-key");
        body.KeyPrefix.ShouldBe(created.TenantApiKey.KeyPrefix);
    }

    [Fact]
    public async Task GetTenantApiKey_NotFound_Returns404()
    {
        var response = await client.SendAsync(AdminRequest(
            HttpMethod.Get,
            $"/admin/tenants/{fixture.TenantId}/tenant-api-keys/{Guid.NewGuid()}"));
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetTenantApiKey_UnknownCredential_Returns401()
    {
        var created = await CreateTenantApiKeyAsync("isolation-key");

        var response = await client.SendAsync(InvalidAdminRequest(
            HttpMethod.Get,
            $"/admin/tenants/{fixture.TenantId}/tenant-api-keys/{created.TenantApiKey.Id}"));
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    // List

    [Fact]
    public async Task ListTenantApiKeys_ReturnsCreatedKeys()
    {
        await CreateTenantApiKeyAsync("list-key-1");
        await CreateTenantApiKeyAsync("list-key-2");

        var response = await client.SendAsync(AdminRequest(
            HttpMethod.Get,
            $"/admin/tenants/{fixture.TenantId}/tenant-api-keys"));
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<TenantApiKeyListDto>(HostJson.Options);
        body.ShouldNotBeNull();
        (body.Items.Count >= 2).ShouldBeTrue();
        foreach (var k in body.Items)
            k.KeyPrefix.ShouldStartWith("intg_", Case.Sensitive);
    }

    [Fact]
    public async Task ListTenantApiKeys_Pagination_WorksCorrectly()
    {
        await CreateTenantApiKeyAsync("page-key-1");
        await CreateTenantApiKeyAsync("page-key-2");
        await CreateTenantApiKeyAsync("page-key-3");

        var page1 = await client.SendAsync(AdminRequest(
            HttpMethod.Get,
            $"/admin/tenants/{fixture.TenantId}/tenant-api-keys?limit=2"));
        page1.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body1 = await page1.Content.ReadFromJsonAsync<TenantApiKeyListDto>(HostJson.Options);
        body1.ShouldNotBeNull();
        body1.Items.Count.ShouldBe(2);
        body1.NextCursor.ShouldNotBeNull();

        var page2 = await client.SendAsync(AdminRequest(
            HttpMethod.Get,
            $"/admin/tenants/{fixture.TenantId}/tenant-api-keys?limit=2&after={Uri.EscapeDataString(body1.NextCursor!)}"));
        page2.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body2 = await page2.Content.ReadFromJsonAsync<TenantApiKeyListDto>(HostJson.Options);
        body2.ShouldNotBeNull();
        (body2.Items.Count >= 1).ShouldBeTrue();

        // Pages must not overlap
        var allIds = body1.Items.Select(k => k.Id).Concat(body2.Items.Select(k => k.Id)).ToList();
        allIds.Distinct().Count().ShouldBe(allIds.Count);
    }

    // Revoke

    [Fact]
    public async Task RevokeTenantApiKey_Returns200_AndKeyIsDisabled()
    {
        var created = await CreateTenantApiKeyAsync("revoke-key");

        var revokeResponse = await client.SendAsync(AdminRequest(
            HttpMethod.Post,
            $"/admin/tenants/{fixture.TenantId}/tenant-api-keys/{created.TenantApiKey.Id}/revoke"));
        revokeResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        var getResponse = await client.SendAsync(AdminRequest(
            HttpMethod.Get,
            $"/admin/tenants/{fixture.TenantId}/tenant-api-keys/{created.TenantApiKey.Id}"));
        getResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await getResponse.Content.ReadFromJsonAsync<TenantApiKeyDto>(HostJson.Options);
        body.ShouldNotBeNull();
        body.Status.ShouldBe("disabled");
    }

    [Fact]
    public async Task RevokeTenantApiKey_AlreadyRevoked_Returns404()
    {
        var created = await CreateTenantApiKeyAsync("double-revoke-key");

        var first = await client.SendAsync(AdminRequest(
            HttpMethod.Post,
            $"/admin/tenants/{fixture.TenantId}/tenant-api-keys/{created.TenantApiKey.Id}/revoke"));
        first.StatusCode.ShouldBe(HttpStatusCode.OK);

        var second = await client.SendAsync(AdminRequest(
            HttpMethod.Post,
            $"/admin/tenants/{fixture.TenantId}/tenant-api-keys/{created.TenantApiKey.Id}/revoke"));
        second.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task RevokeTenantApiKey_UnknownCredential_Returns401()
    {
        var created = await CreateTenantApiKeyAsync("cross-tenant-revoke-key");

        var response = await client.SendAsync(InvalidAdminRequest(
            HttpMethod.Post,
            $"/admin/tenants/{fixture.TenantId}/tenant-api-keys/{created.TenantApiKey.Id}/revoke"));
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    // Helpers

    private Task<HttpResponseMessage> PostTenantApiKeyAsync(string name) =>
        client.SendAsync(AdminRequest(
            HttpMethod.Post,
            $"/admin/tenants/{fixture.TenantId}/tenant-api-keys",
            new { name, description = $"Test key: {name}" }));

    private async Task<CreateTenantApiKeyResult> CreateTenantApiKeyAsync(string name)
    {
        var response = await PostTenantApiKeyAsync(name);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<CreateTenantApiKeyResult>(HostJson.Options);
        return body!;
    }

    private static HttpRequestMessage InvalidAdminRequest(HttpMethod method, string url, object? body = null)
    {
        var msg = new HttpRequestMessage(method, url);
        msg.Headers.TryAddWithoutValidation("Authorization", AdminApiFixture.InvalidOperatorAuthHeader);
        if (body is not null)
            msg.Content = JsonContent.Create(body);
        return msg;
    }
}
