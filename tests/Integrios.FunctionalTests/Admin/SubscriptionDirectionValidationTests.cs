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
    public async Task CreateSubscription_SourceOnlyDestinationConnection_Returns422()
    {
        var topic = await CreateTopicAsync("payments");
        Guid destinationConnectionId = await InsertConnectionWithDirectionAsync("source_only_sink", "source");

        var response = await client.SendAsync(AdminRequest(
            HttpMethod.Post,
            $"/admin/tenants/{fixture.TenantId}/topics/{topic.Id}/subscriptions",
            new
            {
                name = "erp-sink",
                match_rules = new { event_type = "payment.created" },
                destination_connection_id = destinationConnectionId,
                order_index = 10
            }));

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Theory]
    [InlineData("destination")]
    [InlineData("both")]
    public async Task CreateSubscription_DestinationCapableConnection_IsAllowed(string direction)
    {
        var topic = await CreateTopicAsync("payments");
        Guid destinationConnectionId = await InsertConnectionWithDirectionAsync($"allowed_{direction}_sink", direction);

        var response = await client.SendAsync(AdminRequest(
            HttpMethod.Post,
            $"/admin/tenants/{fixture.TenantId}/topics/{topic.Id}/subscriptions",
            new
            {
                name = "erp-sink",
                match_rules = new { event_type = "payment.created" },
                destination_connection_id = destinationConnectionId,
                order_index = 10
            }));

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task CreateSubscription_CrossTenantDestinationConnection_Returns422()
    {
        var topic = await CreateTopicAsync("payments");
        Guid destinationConnectionId = await InsertConnectionWithDirectionAsync(
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
                destination_connection_id = destinationConnectionId,
                order_index = 10
            }));

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task CreateSubscription_MissingDestinationAuthentication_Returns422()
    {
        var topic = await CreateTopicAsync("payments");
        Guid destinationConnectionId = await InsertConnectionWithDirectionAsync(
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
                destination_connection_id = destinationConnectionId,
                order_index = 10
            }));

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task UpdateSubscription_SourceOnlyDestinationConnection_Returns422()
    {
        var topic = await CreateTopicAsync("payments");
        var created = await CreateSubscriptionAsync(topic.Id, "erp-sink", "payment.created");
        Guid destinationConnectionId = await InsertConnectionWithDirectionAsync("source_only_update_sink", "source");

        var response = await client.SendAsync(AdminRequest(
            HttpMethod.Patch,
            $"/admin/tenants/{fixture.TenantId}/topics/{topic.Id}/subscriptions/{created.Id}",
            new
            {
                name = "erp-sink-v2",
                match_rules = new { event_type = "payment.updated" },
                destination_connection_id = destinationConnectionId,
                order_index = 25
            }));

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task UpdateSubscription_CrossTenantDestinationConnection_Returns422()
    {
        var topic = await CreateTopicAsync("payments");
        var created = await CreateSubscriptionAsync(topic.Id, "erp-sink", "payment.created");
        Guid destinationConnectionId = await InsertConnectionWithDirectionAsync(
            "cross_tenant_update_sink",
            "destination",
            fixture.OtherTenantId);

        var response = await client.SendAsync(AdminRequest(
            HttpMethod.Patch,
            $"/admin/tenants/{fixture.TenantId}/topics/{topic.Id}/subscriptions/{created.Id}",
            new
            {
                name = "cross-tenant-sink",
                match_rules = new { event_type = "payment.updated" },
                destination_connection_id = destinationConnectionId,
                order_index = 25
            }));

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Database_CrossTenantDestinationConnection_IsRejected()
    {
        var topic = await CreateTopicAsync("payments");
        Guid destinationConnectionId = await InsertConnectionWithDirectionAsync(
            "cross_tenant_direct_sink",
            "destination",
            fixture.OtherTenantId);

        await using var connection = fixture.CreateConnection();
        await connection.OpenAsync();
        Task insert = connection.ExecuteAsync($$$"""
            INSERT INTO subscriptions (
                id, tenant_id, topic_id, name, match_rules,
                destination_connection_id, status, order_index)
            VALUES (
                @Id, @TenantId, @TopicId, 'cross-tenant-direct',
                {{{fixture.Json("@MatchRules")}}},
                @DestinationConnectionId, 'active', 0);
            """, new
        {
            Id = Guid.NewGuid(),
            fixture.TenantId,
            TopicId = topic.Id,
            MatchRules = "{\"event_type\":\"payment.created\"}",
            DestinationConnectionId = destinationConnectionId
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
                destination_connection_id = fixture.SourceConnectionId,
                order_index = 10
            }));

        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<SubscriptionDto>(HostJson.Options))!;
    }

    private async Task<Guid> InsertConnectionWithDirectionAsync(
        string key,
        string direction,
        Guid? tenantId = null,
        string[]? authenticationSchemes = null)
    {
        Guid connectorId = Guid.NewGuid();
        Guid connectionId = Guid.NewGuid();

        await using var connection = fixture.CreateConnection();
        await connection.OpenAsync();
        await connection.ExecuteAsync($$$"""
            INSERT INTO connectors (
                id, {{{fixture.KeyColumn}}}, contract_version, manifest_schema_version, name, direction,
                status, description, manifest, created_at, updated_at)
            VALUES (
                @Id, @Key, 1, 1, @Name, @Direction,
                'active', 'test connector', {{{fixture.Json("@Manifest")}}}, {{{fixture.Now}}}, {{{fixture.Now}}});
            """, new
        {
            Id = connectorId,
            Key = key,
            Name = key,
            Direction = direction,
            Manifest = TestConnectorManifest.Create(key, key, direction, authenticationSchemes)
        });

        await connection.ExecuteAsync($$$"""
            INSERT INTO connections (id, tenant_id, connector_id, name, config, source_verification, destination_authentication, status, environment, description, created_at, updated_at)
            VALUES (@Id, @TenantId, @ConnectorId, @Name, {{{fixture.Json("@Config")}}}, NULL, NULL, 'active', NULL, NULL, {{{fixture.Now}}}, {{{fixture.Now}}});
            """, new
        {
            Id = connectionId,
            TenantId = tenantId ?? fixture.TenantId,
            ConnectorId = connectorId,
            Name = key,
            Config = "{\"base_uri\":\"http://localhost:5054/sink/custom\"}"
        });

        return connectionId;
    }

    private sealed record SubscriptionDto(
        Guid Id,
        Guid TopicId,
        Guid TenantId,
        string Name,
        JsonElement MatchRules,
        Guid DestinationConnectionId,
        JsonElement? MappingConfig,
        string Status,
        int OrderIndex,
        string? Description,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt);
}
