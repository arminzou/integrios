using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Integrios.Admin.Endpoints;
using Microsoft.AspNetCore.Mvc.Testing;
using Integrios.Tests.Shared;

namespace Integrios.FunctionalTests.Admin;

public sealed class SubscriptionDirectionValidationTests : AdminApiTestBase, IClassFixture<AdminApiFixture>, IAsyncLifetime
{

    private readonly AdminApiFixture fixture;
    private HttpClient client = null!;

    public SubscriptionDirectionValidationTests(AdminApiFixture fixture)
    {
        this.fixture = fixture;
    }

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
    public async Task CreateSubscription_SourceOnlyDestination_Returns422()
    {
        var topic = await CreateTopicAsync("payments");
        Guid destinationId = await InsertDestinationWithDirectionAsync("source_only_sink", "source");

        var response = await client.SendAsync(AdminRequest(
            HttpMethod.Post,
            $"/admin/tenants/{fixture.TenantId}/topics/{topic.Id}/subscriptions",
            new
            {
                name = "erp-sink",
                match_rules = new { event_type = "payment.created" },
                destination_id = destinationId,
                order_index = 10
            }));

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Theory]
    [InlineData("destination")]
    [InlineData("both")]
    public async Task CreateSubscription_DestinationCapableDestination_ReturnsCreated(string direction)
    {
        var topic = await CreateTopicAsync("payments");
        Guid destinationId = await InsertDestinationWithDirectionAsync($"allowed_{direction}_sink", direction);

        var response = await client.SendAsync(AdminRequest(
            HttpMethod.Post,
            $"/admin/tenants/{fixture.TenantId}/topics/{topic.Id}/subscriptions",
            new
            {
                name = "erp-sink",
                match_rules = new { event_type = "payment.created" },
                destination_id = destinationId,
                order_index = 10
            }));

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task CreateSubscription_CrossTenantDestination_Returns422()
    {
        var topic = await CreateTopicAsync("payments");
        Guid destinationId = await InsertDestinationWithDirectionAsync(
            "cross_tenant_create_sink",
            "destination",
            fixture.OtherTenantId);

        var response = await client.SendAsync(AdminRequest(
            HttpMethod.Post,
            $"/admin/tenants/{fixture.TenantId}/topics/{topic.Id}/subscriptions",
            new
            {
                name = "cross-tenant-sink",
                match_rules = new { event_type = "payment.created" },
                destination_id = destinationId,
                order_index = 10
            }));

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task CreateSubscription_MissingDestinationAuthentication_Returns422()
    {
        var topic = await CreateTopicAsync("payments");
        Guid destinationId = await InsertDestinationWithDirectionAsync(
            "authentication_required_sink",
            "destination",
            authenticationSchemes: ["bearer_token"]);

        var response = await client.SendAsync(AdminRequest(
            HttpMethod.Post,
            $"/admin/tenants/{fixture.TenantId}/topics/{topic.Id}/subscriptions",
            new
            {
                name = "missing-authentication",
                match_rules = new { event_type = "payment.created" },
                destination_id = destinationId,
                order_index = 10
            }));

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task UpdateSubscription_SourceOnlyDestination_Returns422()
    {
        var topic = await CreateTopicAsync("payments");
        var created = await CreateSubscriptionAsync(topic.Id, "erp-sink", "payment.created");
        Guid destinationId = await InsertDestinationWithDirectionAsync("source_only_update_sink", "source");

        var response = await client.SendAsync(AdminRequest(
            HttpMethod.Put,
            $"/admin/tenants/{fixture.TenantId}/topics/{topic.Id}/subscriptions/{created.Id}",
            new
            {
                name = "erp-sink-v2",
                match_rules = new { event_type = "payment.updated" },
                destination_id = destinationId,
                order_index = 25,
                mapping = (object?)null,
                http_delivery = (object?)null,
                http_success = (object?)null,
                description = (string?)null,
            }));

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task UpdateSubscription_CrossTenantDestination_Returns422()
    {
        var topic = await CreateTopicAsync("payments");
        var created = await CreateSubscriptionAsync(topic.Id, "erp-sink", "payment.created");
        Guid destinationId = await InsertDestinationWithDirectionAsync(
            "cross_tenant_update_sink",
            "destination",
            fixture.OtherTenantId);

        var response = await client.SendAsync(AdminRequest(
            HttpMethod.Put,
            $"/admin/tenants/{fixture.TenantId}/topics/{topic.Id}/subscriptions/{created.Id}",
            new
            {
                name = "cross-tenant-sink",
                match_rules = new { event_type = "payment.updated" },
                destination_id = destinationId,
                order_index = 25,
                mapping = (object?)null,
                http_delivery = (object?)null,
                http_success = (object?)null,
                description = (string?)null,
            }));

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Database_CrossTenantDestination_IsRejected()
    {
        var topic = await CreateTopicAsync("payments");
        Guid destinationId = await InsertDestinationWithDirectionAsync(
            "cross_tenant_direct_sink",
            "destination",
            fixture.OtherTenantId);

        await using var connection = fixture.CreateConnection();
        await connection.OpenAsync();
        Task insert = connection.ExecuteAsync($$$"""
            INSERT INTO subscriptions (
                id, tenant_id, topic_id, name, match_rules,
                destination_id, status, order_index)
            VALUES (
                @Id, @TenantId, @TopicId, 'cross-tenant-direct',
                {{{fixture.Json("@MatchRules")}}},
                @DestinationId, 'active', 0);
            """, new
        {
            Id = Guid.NewGuid(),
            fixture.TenantId,
            TopicId = topic.Id,
            MatchRules = "{\"event_type\":\"payment.created\"}",
            DestinationId = destinationId
        });

        await Should.ThrowAsync<DbException>(() => insert);
    }

    private async Task<AdminTopicResponse> CreateTopicAsync(string name)
    {
        var response = await client.SendAsync(AdminRequest(
            HttpMethod.Post,
            $"/admin/tenants/{fixture.TenantId}/topics",
            new { name }));

        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AdminTopicResponse>(HostJson.Options))!;
    }

    private async Task<SubscriptionDto> CreateSubscriptionAsync(Guid topicId, string name, string eventType)
    {
        var response = await client.SendAsync(AdminRequest(
            HttpMethod.Post,
            $"/admin/tenants/{fixture.TenantId}/topics/{topicId}/subscriptions",
            new
            {
                name,
                match_rules = new { event_type = eventType },
                destination_id = fixture.DestinationId,
                order_index = 10
            }));

        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<SubscriptionDto>(HostJson.Options))!;
    }

    private async Task<Guid> InsertDestinationWithDirectionAsync(
        string key,
        string direction,
        Guid? tenantId = null,
        string[]? authenticationSchemes = null)
    {
        Guid connectorId = await fixture.ApplyConnectorManifestAsync(
            key, TestConnectorManifest.Create(key, key, direction, authenticationSchemes));
        Guid destinationId = Guid.NewGuid();

        await using var connection = fixture.CreateConnection();
        await connection.OpenAsync();
        await connection.ExecuteAsync($$$"""
            INSERT INTO destinations (id, tenant_id, connector_id, name, configuration, authentication, status, environment, description, created_at, updated_at)
            VALUES (@Id, @TenantId, @ConnectorId, @Name, {{{fixture.Json("@Config")}}}, NULL, 'active', NULL, NULL, {{{fixture.Now}}}, {{{fixture.Now}}});
            """, new
        {
            Id = destinationId,
            TenantId = tenantId ?? fixture.TenantId,
            ConnectorId = connectorId,
            Name = key,
            Config = "{\"base_uri\":\"http://localhost:5054/sink/custom\"}"
        });

        return destinationId;
    }

    private sealed record SubscriptionDto(
        Guid Id,
        Guid TopicId,
        Guid TenantId,
        string Name,
        JsonElement MatchRules,
        Guid DestinationId,
        JsonElement? MappingConfig,
        string Status,
        int OrderIndex,
        string? Description,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt);
}
