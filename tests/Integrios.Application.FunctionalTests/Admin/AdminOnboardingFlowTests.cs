using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Integrios.Application.TenantApiKeys;
using Integrios.Application.Connections;
using Integrios.Application.Tenants;
using Integrios.Admin.Endpoints;
using Microsoft.AspNetCore.Mvc.Testing;
using Integrios.Tests.Shared;

namespace Integrios.Application.FunctionalTests.Admin;

public sealed class AdminOnboardingFlowTests : AdminApiTestBase, IClassFixture<AdminApiFixture>, IAsyncLifetime
{
    private static readonly Guid HttpConnectorId = Guid.Parse("00000000-0000-0000-0000-000000000001");

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

        var tenant = await tenantResponse.Content.ReadFromJsonAsync<TenantDto>(HostJson.Options);
        Assert.NotNull(tenant);

        var tenantApiKeyResponse = await client.SendAsync(AdminRequest(
            HttpMethod.Post,
            $"/admin/tenants/{tenant.Id}/tenant-api-keys",
            new
            {
                name = "acme-ingestion",
                description = "Ingestion automation key"
            }));
        Assert.Equal(HttpStatusCode.Created, tenantApiKeyResponse.StatusCode);

        var tenantApiKey = await tenantApiKeyResponse.Content.ReadFromJsonAsync<CreateTenantApiKeyResult>(HostJson.Options);
        Assert.NotNull(tenantApiKey);
        Assert.False(string.IsNullOrWhiteSpace(tenantApiKey.Token));
        Assert.Equal("acme-ingestion", tenantApiKey.TenantApiKey.Name);

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
                source_connection_ids = new[] { sourceConnection.Id }
            }));
        Assert.Equal(HttpStatusCode.Created, topicResponse.StatusCode);

        var topic = await topicResponse.Content.ReadFromJsonAsync<AdminTopicResponse>(HostJson.Options);
        Assert.NotNull(topic);

        var subscriptionResponse = await client.SendAsync(AdminRequest(
            HttpMethod.Post,
            $"/admin/tenants/{tenant.Id}/topics/{topic.Id}/subscriptions",
            new
            {
                name = "acme-erp-subscription",
                match_rules = new { event_type = "payment.created" },
                destination_connection_id = destinationConnection.Id,
                order_index = 10,
                description = "ERP sink"
            }));
        Assert.Equal(HttpStatusCode.Created, subscriptionResponse.StatusCode);

        var subscription = await subscriptionResponse.Content.ReadFromJsonAsync<SubscriptionDto>(HostJson.Options);
        Assert.NotNull(subscription);
        Assert.Equal(destinationConnection.Id, subscription.DestinationConnectionId);

        var listTopics = await client.SendAsync(AdminRequest(HttpMethod.Get, $"/admin/tenants/{tenant.Id}/topics"));
        Assert.Equal(HttpStatusCode.OK, listTopics.StatusCode);

        var listSubscriptions = await client.SendAsync(AdminRequest(HttpMethod.Get, $"/admin/tenants/{tenant.Id}/topics/{topic.Id}/subscriptions"));
        Assert.Equal(HttpStatusCode.OK, listSubscriptions.StatusCode);
    }

    [Fact]
    public async Task CreateTenant_AcceptsSixtyThreeCharacterDnsLabel()
    {
        string slug = "a" + new string('b', 61) + "z";

        var response = await client.SendAsync(AdminRequest(
            HttpMethod.Post,
            "/admin/tenants",
            new { slug, name = "Boundary Tenant" }));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Theory]
    [InlineData("Uppercase")]
    [InlineData("-leading")]
    [InlineData("trailing-")]
    [InlineData("has_underscore")]
    public async Task CreateTenant_RejectsInvalidSecretNamespaceSlug(string slug)
    {
        var response = await client.SendAsync(AdminRequest(
            HttpMethod.Post,
            "/admin/tenants",
            new { slug, name = "Invalid Tenant" }));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    private async Task<ConnectionDto> CreateConnectionAsync(Guid tenantId, string name, string url, string environment)
    {
        var response = await client.SendAsync(AdminRequest(
            HttpMethod.Post,
            $"/admin/tenants/{tenantId}/connections",
            new
            {
                connector_id = HttpConnectorId,
                name,
                config = new { base_uri = url },
                environment,
                description = $"Connection {name}"
            }));

        response.EnsureSuccessStatusCode();
        var connection = await response.Content.ReadFromJsonAsync<ConnectionDto>(HostJson.Options);
        return connection!;
    }

    private sealed record SubscriptionDto(
        Guid Id,
        Guid TopicId,
        Guid TenantId,
        string Name,
        JsonElement MatchRules,
        Guid DestinationConnectionId,
        string Status,
        int OrderIndex,
        string? Description);
}
