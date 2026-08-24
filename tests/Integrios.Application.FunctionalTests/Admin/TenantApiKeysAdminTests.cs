using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Integrios.Application.Authoring.TenantApiKeys;
using Microsoft.AspNetCore.Mvc.Testing;
using Integrios.Tests.Shared;

namespace Integrios.Application.FunctionalTests.Admin;

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

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(response.Headers.Location);

        var body = await response.Content.ReadFromJsonAsync<CreateTenantApiKeyResult>(HostJson.Options);
        Assert.NotNull(body);
        Assert.Equal("ingest-key", body.TenantApiKey.Name);
        Assert.Equal(fixture.TenantId, body.TenantApiKey.TenantId);
        Assert.Equal("active", body.TenantApiKey.Status);

        // Token format: intg_<64hex>
        Assert.StartsWith("intg_", body.Token, StringComparison.Ordinal);
        Assert.Equal(69, body.Token.Length); // "intg_" (5) + 64 hex chars

        // KeyPrefix is the display hint: first 12 chars of the token
        Assert.Equal(body.Token[..12], body.TenantApiKey.KeyPrefix);
    }

    [Fact]
    public async Task CreateTenantApiKey_ResponseDoesNotExposeScopes()
    {
        var response = await PostTenantApiKeyAsync("authority-contract-key");
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(body.RootElement.GetProperty("tenant_api_key").TryGetProperty("scopes", out _));
    }

    // Get

    [Fact]
    public async Task GetTenantApiKey_ReturnsKey_WithKeyPrefix()
    {
        var created = await CreateTenantApiKeyAsync("get-test-key");

        var response = await client.SendAsync(AdminRequest(
            HttpMethod.Get,
            $"/admin/tenants/{fixture.TenantId}/tenant-api-keys/{created.TenantApiKey.Id}"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<TenantApiKeyDto>(HostJson.Options);
        Assert.NotNull(body);
        Assert.Equal(created.TenantApiKey.Id, body.Id);
        Assert.Equal("get-test-key", body.Name);
        Assert.Equal(created.TenantApiKey.KeyPrefix, body.KeyPrefix);
    }

    [Fact]
    public async Task GetTenantApiKey_NotFound_Returns404()
    {
        var response = await client.SendAsync(AdminRequest(
            HttpMethod.Get,
            $"/admin/tenants/{fixture.TenantId}/tenant-api-keys/{Guid.NewGuid()}"));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetTenantApiKey_UnknownCredential_Returns401()
    {
        var created = await CreateTenantApiKeyAsync("isolation-key");

        var response = await client.SendAsync(InvalidAdminRequest(
            HttpMethod.Get,
            $"/admin/tenants/{fixture.TenantId}/tenant-api-keys/{created.TenantApiKey.Id}"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
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
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<TenantApiKeyListDto>(HostJson.Options);
        Assert.NotNull(body);
        Assert.True(body.Items.Count >= 2);
        Assert.All(body.Items, k => Assert.StartsWith("intg_", k.KeyPrefix, StringComparison.Ordinal));
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
        Assert.Equal(HttpStatusCode.OK, page1.StatusCode);

        var body1 = await page1.Content.ReadFromJsonAsync<TenantApiKeyListDto>(HostJson.Options);
        Assert.NotNull(body1);
        Assert.Equal(2, body1.Items.Count);
        Assert.NotNull(body1.NextCursor);

        var page2 = await client.SendAsync(AdminRequest(
            HttpMethod.Get,
            $"/admin/tenants/{fixture.TenantId}/tenant-api-keys?limit=2&after={Uri.EscapeDataString(body1.NextCursor!)}"));
        Assert.Equal(HttpStatusCode.OK, page2.StatusCode);

        var body2 = await page2.Content.ReadFromJsonAsync<TenantApiKeyListDto>(HostJson.Options);
        Assert.NotNull(body2);
        Assert.True(body2.Items.Count >= 1);

        // Pages must not overlap
        var allIds = body1.Items.Select(k => k.Id).Concat(body2.Items.Select(k => k.Id)).ToList();
        Assert.Equal(allIds.Count, allIds.Distinct().Count());
    }

    // Revoke

    [Fact]
    public async Task RevokeTenantApiKey_Returns200_AndKeyIsDisabled()
    {
        var created = await CreateTenantApiKeyAsync("revoke-key");

        var revokeResponse = await client.SendAsync(AdminRequest(
            HttpMethod.Post,
            $"/admin/tenants/{fixture.TenantId}/tenant-api-keys/{created.TenantApiKey.Id}/revoke"));
        Assert.Equal(HttpStatusCode.OK, revokeResponse.StatusCode);

        var getResponse = await client.SendAsync(AdminRequest(
            HttpMethod.Get,
            $"/admin/tenants/{fixture.TenantId}/tenant-api-keys/{created.TenantApiKey.Id}"));
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);

        var body = await getResponse.Content.ReadFromJsonAsync<TenantApiKeyDto>(HostJson.Options);
        Assert.NotNull(body);
        Assert.Equal("disabled", body.Status);
    }

    [Fact]
    public async Task RevokeTenantApiKey_AlreadyRevoked_Returns404()
    {
        var created = await CreateTenantApiKeyAsync("double-revoke-key");

        var first = await client.SendAsync(AdminRequest(
            HttpMethod.Post,
            $"/admin/tenants/{fixture.TenantId}/tenant-api-keys/{created.TenantApiKey.Id}/revoke"));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var second = await client.SendAsync(AdminRequest(
            HttpMethod.Post,
            $"/admin/tenants/{fixture.TenantId}/tenant-api-keys/{created.TenantApiKey.Id}/revoke"));
        Assert.Equal(HttpStatusCode.NotFound, second.StatusCode);
    }

    [Fact]
    public async Task RevokeTenantApiKey_UnknownCredential_Returns401()
    {
        var created = await CreateTenantApiKeyAsync("cross-tenant-revoke-key");

        var response = await client.SendAsync(InvalidAdminRequest(
            HttpMethod.Post,
            $"/admin/tenants/{fixture.TenantId}/tenant-api-keys/{created.TenantApiKey.Id}/revoke"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
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
