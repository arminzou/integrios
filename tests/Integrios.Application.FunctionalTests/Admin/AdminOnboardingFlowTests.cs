using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Integrios.Application.Authoring.TenantApiKeys;
using Integrios.Application.Authoring.Connections;
using Integrios.Application.Authoring.Tenants;
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
        tenantResponse.StatusCode.ShouldBe(HttpStatusCode.Created);

        var tenant = await tenantResponse.Content.ReadFromJsonAsync<TenantDto>(HostJson.Options);
        tenant.ShouldNotBeNull();

        var tenantApiKeyResponse = await client.SendAsync(AdminRequest(
            HttpMethod.Post,
            $"/admin/tenants/{tenant.Id}/tenant-api-keys",
            new
            {
                name = "acme-ingestion",
                description = "Ingestion automation key"
            }));
        tenantApiKeyResponse.StatusCode.ShouldBe(HttpStatusCode.Created);

        var tenantApiKey = await tenantApiKeyResponse.Content.ReadFromJsonAsync<CreateTenantApiKeyResult>(HostJson.Options);
        tenantApiKey.ShouldNotBeNull();
        string.IsNullOrWhiteSpace(tenantApiKey.Token).ShouldBeFalse();
        tenantApiKey.TenantApiKey.Name.ShouldBe("acme-ingestion");

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
        topicResponse.StatusCode.ShouldBe(HttpStatusCode.Created);

        var topic = await topicResponse.Content.ReadFromJsonAsync<AdminTopicResponse>(HostJson.Options);
        topic.ShouldNotBeNull();

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
        subscriptionResponse.StatusCode.ShouldBe(HttpStatusCode.Created);

        var subscription = await subscriptionResponse.Content.ReadFromJsonAsync<SubscriptionDto>(HostJson.Options);
        subscription.ShouldNotBeNull();
        subscription.DestinationConnectionId.ShouldBe(destinationConnection.Id);

        var listTopics = await client.SendAsync(AdminRequest(HttpMethod.Get, $"/admin/tenants/{tenant.Id}/topics"));
        listTopics.StatusCode.ShouldBe(HttpStatusCode.OK);

        var listSubscriptions = await client.SendAsync(AdminRequest(HttpMethod.Get, $"/admin/tenants/{tenant.Id}/topics/{topic.Id}/subscriptions"));
        listSubscriptions.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task CreateTenant_AcceptsSixtyThreeCharacterDnsLabel()
    {
        string slug = "a" + new string('b', 61) + "z";

        var response = await client.SendAsync(AdminRequest(
            HttpMethod.Post,
            "/admin/tenants",
            new { slug, name = "Boundary Tenant" }));

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
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

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
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
