using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Integrios.Application.Connections;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Integrios.Admin.Tests;

public sealed class ConnectionsAdminTests : IClassFixture<AdminApiFixture>, IAsyncLifetime
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
    private static readonly Guid WebhookIntegrationId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    private readonly AdminApiFixture fixture;
    private HttpClient client = null!;

    public ConnectionsAdminTests(AdminApiFixture fixture)
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

    [Fact]
    public async Task CreateConnection_ReturnsCreated_WithCorrectBody()
    {
        var response = await PostConnectionAsync("erp-sink", "http://localhost:5054/sink/erp");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(response.Headers.Location);

        var body = await response.Content.ReadFromJsonAsync<ConnectionResponse>(WebJson);
        Assert.NotNull(body);
        Assert.Equal(fixture.TenantId, body.TenantId);
        Assert.Equal("erp-sink", body.Name);
        Assert.Equal("active", body.Status);
        Assert.NotEqual(default, body.Id);
    }

    [Fact]
    public async Task CreateConnection_DuplicateName_ReturnsConflict()
    {
        var first = await PostConnectionAsync("erp-sink", "http://localhost:5054/sink/erp");
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var second = await PostConnectionAsync("erp-sink", "http://localhost:5054/sink/erp-2");
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task CreateConnection_SameName_DifferentTenant_ReturnsCreated()
    {
        // Create a second tenant
        var tenantResponse = await client.SendAsync(AdminRequest(
            HttpMethod.Post, "/admin/tenants",
            new { slug = "other-corp", name = "Other Corp" }));
        Assert.Equal(HttpStatusCode.Created, tenantResponse.StatusCode);
        var otherTenant = await tenantResponse.Content.ReadFromJsonAsync<JsonElement>(WebJson);
        var otherTenantId = otherTenant.GetProperty("id").GetGuid();

        // Same name in the fixture tenant
        var first = await PostConnectionAsync("shared-name", "http://localhost:5054/sink/a");
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        // Same name in the other tenant should be allowed
        var second = await client.SendAsync(AdminRequest(
            HttpMethod.Post, $"/admin/tenants/{otherTenantId}/connections",
            new
            {
                integrationId = WebhookIntegrationId,
                name = "shared-name",
                config = new { url = "http://localhost:5054/sink/b" }
            }));
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
    }

    [Fact]
    public async Task CreateConnection_UnknownIntegration_Returns422()
    {
        var response = await client.SendAsync(AdminRequest(
            HttpMethod.Post, $"/admin/tenants/{fixture.TenantId}/connections",
            new
            {
                integrationId = Guid.NewGuid(),
                name = "bad-connection",
                config = new { url = "http://localhost:5054/sink/x" }
            }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    private Task<HttpResponseMessage> PostConnectionAsync(string name, string url) =>
        client.SendAsync(AdminRequest(
            HttpMethod.Post,
            $"/admin/tenants/{fixture.TenantId}/connections",
            new
            {
                integrationId = WebhookIntegrationId,
                name,
                config = new { url }
            }));

    private HttpRequestMessage AdminRequest(HttpMethod method, string url, object? body = null)
    {
        var msg = new HttpRequestMessage(method, url);
        msg.Headers.TryAddWithoutValidation("Authorization", AdminApiFixture.GlobalAdminAuthHeader);
        if (body is not null)
            msg.Content = JsonContent.Create(body);
        return msg;
    }
}
