using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Integrios.Application.ApiKeys;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Integrios.Admin.Tests;

public sealed class ApiKeysAdminTests : IClassFixture<AdminApiFixture>, IAsyncLifetime
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private readonly AdminApiFixture fixture;
    private HttpClient client = null!;

    public ApiKeysAdminTests(AdminApiFixture fixture)
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
    public async Task CreateApiKey_ReturnsCreated_WithTokenAndKeyPrefix()
    {
        var response = await PostApiKeyAsync("ingest-key");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(response.Headers.Location);

        var body = await response.Content.ReadFromJsonAsync<CreateApiKeyResult>(WebJson);
        Assert.NotNull(body);
        Assert.Equal("ingest-key", body.ApiKey.Name);
        Assert.Equal(fixture.TenantId, body.ApiKey.TenantId);
        Assert.Equal("active", body.ApiKey.Status);

        // Token format: intg_<64hex>
        Assert.StartsWith("intg_", body.Token, StringComparison.Ordinal);
        Assert.Equal(69, body.Token.Length); // "intg_" (5) + 64 hex chars

        // KeyPrefix is the display hint: first 12 chars of the token
        Assert.Equal(body.Token[..12], body.ApiKey.KeyPrefix);
    }

    [Fact]
    public async Task CreateApiKey_ResponseDoesNotExposeScopes()
    {
        var response = await PostApiKeyAsync("authority-contract-key");
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(body.RootElement.GetProperty("apiKey").TryGetProperty("scopes", out _));
    }

    // Get

    [Fact]
    public async Task GetApiKey_ReturnsKey_WithKeyPrefix()
    {
        var created = await CreateApiKeyAsync("get-test-key");

        var response = await client.SendAsync(AdminRequest(
            HttpMethod.Get,
            $"/admin/tenants/{fixture.TenantId}/api-keys/{created.ApiKey.Id}"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<ApiKeyDto>(WebJson);
        Assert.NotNull(body);
        Assert.Equal(created.ApiKey.Id, body.Id);
        Assert.Equal("get-test-key", body.Name);
        Assert.Equal(created.ApiKey.KeyPrefix, body.KeyPrefix);
    }

    [Fact]
    public async Task GetApiKey_NotFound_Returns404()
    {
        var response = await client.SendAsync(AdminRequest(
            HttpMethod.Get,
            $"/admin/tenants/{fixture.TenantId}/api-keys/{Guid.NewGuid()}"));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetApiKey_UnknownCredential_Returns401()
    {
        var created = await CreateApiKeyAsync("isolation-key");

        var response = await client.SendAsync(InvalidAdminRequest(
            HttpMethod.Get,
            $"/admin/tenants/{fixture.TenantId}/api-keys/{created.ApiKey.Id}"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // List

    [Fact]
    public async Task ListApiKeys_ReturnsCreatedKeys()
    {
        await CreateApiKeyAsync("list-key-1");
        await CreateApiKeyAsync("list-key-2");

        var response = await client.SendAsync(AdminRequest(
            HttpMethod.Get,
            $"/admin/tenants/{fixture.TenantId}/api-keys"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<ApiKeyListDto>(WebJson);
        Assert.NotNull(body);
        Assert.True(body.Items.Count >= 2);
        Assert.All(body.Items, k => Assert.StartsWith("intg_", k.KeyPrefix, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ListApiKeys_Pagination_WorksCorrectly()
    {
        await CreateApiKeyAsync("page-key-1");
        await CreateApiKeyAsync("page-key-2");
        await CreateApiKeyAsync("page-key-3");

        var page1 = await client.SendAsync(AdminRequest(
            HttpMethod.Get,
            $"/admin/tenants/{fixture.TenantId}/api-keys?limit=2"));
        Assert.Equal(HttpStatusCode.OK, page1.StatusCode);

        var body1 = await page1.Content.ReadFromJsonAsync<ApiKeyListDto>(WebJson);
        Assert.NotNull(body1);
        Assert.Equal(2, body1.Items.Count);
        Assert.NotNull(body1.NextCursor);

        var page2 = await client.SendAsync(AdminRequest(
            HttpMethod.Get,
            $"/admin/tenants/{fixture.TenantId}/api-keys?limit=2&after={Uri.EscapeDataString(body1.NextCursor!)}"));
        Assert.Equal(HttpStatusCode.OK, page2.StatusCode);

        var body2 = await page2.Content.ReadFromJsonAsync<ApiKeyListDto>(WebJson);
        Assert.NotNull(body2);
        Assert.True(body2.Items.Count >= 1);

        // Pages must not overlap
        var allIds = body1.Items.Select(k => k.Id).Concat(body2.Items.Select(k => k.Id)).ToList();
        Assert.Equal(allIds.Count, allIds.Distinct().Count());
    }

    // Revoke

    [Fact]
    public async Task RevokeApiKey_Returns200_AndKeyIsDisabled()
    {
        var created = await CreateApiKeyAsync("revoke-key");

        var revokeResponse = await client.SendAsync(AdminRequest(
            HttpMethod.Post,
            $"/admin/tenants/{fixture.TenantId}/api-keys/{created.ApiKey.Id}/revoke"));
        Assert.Equal(HttpStatusCode.OK, revokeResponse.StatusCode);

        var getResponse = await client.SendAsync(AdminRequest(
            HttpMethod.Get,
            $"/admin/tenants/{fixture.TenantId}/api-keys/{created.ApiKey.Id}"));
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);

        var body = await getResponse.Content.ReadFromJsonAsync<ApiKeyDto>(WebJson);
        Assert.NotNull(body);
        Assert.Equal("disabled", body.Status);
    }

    [Fact]
    public async Task RevokeApiKey_AlreadyRevoked_Returns404()
    {
        var created = await CreateApiKeyAsync("double-revoke-key");

        var first = await client.SendAsync(AdminRequest(
            HttpMethod.Post,
            $"/admin/tenants/{fixture.TenantId}/api-keys/{created.ApiKey.Id}/revoke"));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var second = await client.SendAsync(AdminRequest(
            HttpMethod.Post,
            $"/admin/tenants/{fixture.TenantId}/api-keys/{created.ApiKey.Id}/revoke"));
        Assert.Equal(HttpStatusCode.NotFound, second.StatusCode);
    }

    [Fact]
    public async Task RevokeApiKey_UnknownCredential_Returns401()
    {
        var created = await CreateApiKeyAsync("cross-tenant-revoke-key");

        var response = await client.SendAsync(InvalidAdminRequest(
            HttpMethod.Post,
            $"/admin/tenants/{fixture.TenantId}/api-keys/{created.ApiKey.Id}/revoke"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // Helpers

    private Task<HttpResponseMessage> PostApiKeyAsync(string name) =>
        client.SendAsync(AdminRequest(
            HttpMethod.Post,
            $"/admin/tenants/{fixture.TenantId}/api-keys",
            new { name, description = $"Test key: {name}" }));

    private async Task<CreateApiKeyResult> CreateApiKeyAsync(string name)
    {
        var response = await PostApiKeyAsync(name);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<CreateApiKeyResult>(WebJson);
        return body!;
    }

    private HttpRequestMessage AdminRequest(HttpMethod method, string url, object? body = null)
    {
        var msg = new HttpRequestMessage(method, url);
        msg.Headers.TryAddWithoutValidation("Authorization", AdminApiFixture.GlobalAdminAuthHeader);
        if (body is not null)
            msg.Content = JsonContent.Create(body);
        return msg;
    }

    private static HttpRequestMessage InvalidAdminRequest(HttpMethod method, string url, object? body = null)
    {
        var msg = new HttpRequestMessage(method, url);
        msg.Headers.TryAddWithoutValidation("Authorization", AdminApiFixture.InvalidAdminAuthHeader);
        if (body is not null)
            msg.Content = JsonContent.Create(body);
        return msg;
    }
}
