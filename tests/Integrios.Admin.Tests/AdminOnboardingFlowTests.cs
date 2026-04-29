using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Integrios.Application.ApiKeys;
using Integrios.Application.Connections;
using Integrios.Application.Tenants;
using Integrios.Application.Topics;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Integrios.Admin.Tests;

public sealed class AdminOnboardingFlowTests : IClassFixture<AdminApiFixture>, IAsyncLifetime
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
    private static readonly Guid WebhookIntegrationId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    private readonly AdminApiFixture fixture;
    private HttpClient client = null!;

    public AdminOnboardingFlowTests(AdminApiFixture fixture)
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
    public async Task GlobalAdmin_CanOnboardTenant_FromScratch()
    {
        var tenantResponse = await client.SendAsync(AdminRequest(
            HttpMethod.Post,
            "/admin/tenants",
            new
            {
                slug = "acme-corp",
                name = "Acme Corp",
                environment = "production",
                description = "Customer tenant"
            }));
        Assert.Equal(HttpStatusCode.Created, tenantResponse.StatusCode);

        var tenant = await tenantResponse.Content.ReadFromJsonAsync<TenantResponse>(WebJson);
        Assert.NotNull(tenant);

        var apiKeyResponse = await client.SendAsync(AdminRequest(
            HttpMethod.Post,
            $"/admin/tenants/{tenant.Id}/api-keys",
            new
            {
                name = "acme-ingress",
                scopes = new[] { "events:write" },
                description = "Ingress automation key"
            }));
        Assert.Equal(HttpStatusCode.Created, apiKeyResponse.StatusCode);

        var apiKey = await apiKeyResponse.Content.ReadFromJsonAsync<CreateApiKeyResponse>(WebJson);
        Assert.NotNull(apiKey);
        Assert.False(string.IsNullOrWhiteSpace(apiKey.Secret));
        Assert.Equal("acme-ingress", apiKey.Key.Name);

        var sourceConnection = await CreateConnectionAsync(
            tenant.Id,
            "acme-source",
            "http://localhost:5054/sink/acme-source",
            "production");

        var destinationConnection = await CreateConnectionAsync(
            tenant.Id,
            "acme-erp",
            "http://localhost:5054/sink/acme-erp",
            "production");

        var topicResponse = await client.SendAsync(AdminRequest(
            HttpMethod.Post,
            $"/admin/tenants/{tenant.Id}/topics",
            new
            {
                name = "payments",
                description = "Payment events",
                sourceConnectionIds = new[] { sourceConnection.Id }
            }));
        Assert.Equal(HttpStatusCode.Created, topicResponse.StatusCode);

        var topic = await topicResponse.Content.ReadFromJsonAsync<TopicResponse>(WebJson);
        Assert.NotNull(topic);
        Assert.Equal([sourceConnection.Id], topic.SourceConnectionIds);

        var subscriptionResponse = await client.SendAsync(AdminRequest(
            HttpMethod.Post,
            $"/admin/tenants/{tenant.Id}/topics/{topic.Id}/subscriptions",
            new
            {
                name = "acme-erp-subscription",
                matchRules = new { event_type = "payment.created" },
                destinationConnectionId = destinationConnection.Id,
                dlqEnabled = true,
                orderIndex = 10,
                description = "ERP sink"
            }));
        Assert.Equal(HttpStatusCode.Created, subscriptionResponse.StatusCode);

        var subscription = await subscriptionResponse.Content.ReadFromJsonAsync<SubscriptionResponse>(WebJson);
        Assert.NotNull(subscription);
        Assert.Equal(destinationConnection.Id, subscription.DestinationConnectionId);
        Assert.True(subscription.DlqEnabled);

        var listTopics = await client.SendAsync(AdminRequest(HttpMethod.Get, $"/admin/tenants/{tenant.Id}/topics"));
        Assert.Equal(HttpStatusCode.OK, listTopics.StatusCode);

        var listSubscriptions = await client.SendAsync(AdminRequest(HttpMethod.Get, $"/admin/tenants/{tenant.Id}/topics/{topic.Id}/subscriptions"));
        Assert.Equal(HttpStatusCode.OK, listSubscriptions.StatusCode);
    }

    private async Task<ConnectionResponse> CreateConnectionAsync(Guid tenantId, string name, string url, string environment)
    {
        var response = await client.SendAsync(AdminRequest(
            HttpMethod.Post,
            $"/admin/tenants/{tenantId}/connections",
            new
            {
                integrationId = WebhookIntegrationId,
                name,
                config = new { url },
                environment,
                description = $"Connection {name}"
            }));

        response.EnsureSuccessStatusCode();
        var connection = await response.Content.ReadFromJsonAsync<ConnectionResponse>(WebJson);
        return connection!;
    }

    private HttpRequestMessage AdminRequest(HttpMethod method, string url, object? body = null)
    {
        var msg = new HttpRequestMessage(method, url);
        msg.Headers.TryAddWithoutValidation("Authorization", AdminApiFixture.GlobalAdminAuthHeader);
        if (body is not null)
            msg.Content = JsonContent.Create(body);
        return msg;
    }

    private sealed record SubscriptionResponse(
        Guid Id,
        Guid TopicId,
        Guid TenantId,
        string Name,
        JsonElement MatchRules,
        Guid DestinationConnectionId,
        bool DlqEnabled,
        string Status,
        int OrderIndex,
        string? Description);
}
