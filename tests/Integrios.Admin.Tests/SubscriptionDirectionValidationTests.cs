using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Integrios.Admin.Endpoints;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;

namespace Integrios.Admin.Tests;

public sealed class SubscriptionDirectionValidationTests : IClassFixture<AdminApiFixture>, IAsyncLifetime
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

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
                matchRules = new { event_type = "payment.created" },
                destinationConnectionId,
                orderIndex = 10
            }));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
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
                matchRules = new { event_type = "payment.created" },
                destinationConnectionId,
                orderIndex = 10
            }));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
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
                matchRules = new { event_type = "payment.created" },
                destinationConnectionId,
                orderIndex = 10
            }));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
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
                matchRules = new { event_type = "payment.created" },
                destinationConnectionId,
                orderIndex = 10
            }));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
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
                matchRules = new { event_type = "payment.updated" },
                destinationConnectionId,
                orderIndex = 25
            }));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
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
                matchRules = new { event_type = "payment.updated" },
                destinationConnectionId,
                orderIndex = 25
            }));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task Database_CrossTenantDestinationConnection_IsRejected()
    {
        var topic = await CreateTopicAsync("payments");
        Guid destinationConnectionId = await InsertConnectionWithDirectionAsync(
            "cross_tenant_direct_sink",
            "destination",
            fixture.OtherTenantId);

        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO subscriptions (
                id, tenant_id, topic_id, name, match_rules,
                destination_connection_id, status, order_index)
            VALUES (
                @Id, @TenantId, @TopicId, 'cross-tenant-direct',
                '{"event_type":"payment.created"}'::jsonb,
                @DestinationConnectionId, 'active', 0);
            """,
            connection);
        command.Parameters.AddWithValue("Id", Guid.NewGuid());
        command.Parameters.AddWithValue("TenantId", fixture.TenantId);
        command.Parameters.AddWithValue("TopicId", topic.Id);
        command.Parameters.AddWithValue("DestinationConnectionId", destinationConnectionId);

        var exception = await Assert.ThrowsAsync<PostgresException>(async () =>
            await command.ExecuteNonQueryAsync());

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, exception.SqlState);
        Assert.Equal("fk_subscriptions_destination_connection_tenant", exception.ConstraintName);
    }

    private async Task<AdminTopicResponse> CreateTopicAsync(string name)
    {
        var response = await client.SendAsync(AdminRequest(
            HttpMethod.Post,
            $"/admin/tenants/{fixture.TenantId}/topics",
            new { name }));

        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AdminTopicResponse>(WebJson))!;
    }

    private async Task<SubscriptionDto> CreateSubscriptionAsync(Guid topicId, string name, string eventType)
    {
        var response = await client.SendAsync(AdminRequest(
            HttpMethod.Post,
            $"/admin/tenants/{fixture.TenantId}/topics/{topicId}/subscriptions",
            new
            {
                name,
                matchRules = new { event_type = eventType },
                destinationConnectionId = fixture.SourceConnectionId,
                orderIndex = 10
            }));

        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<SubscriptionDto>(WebJson))!;
    }

    private HttpRequestMessage AdminRequest(HttpMethod method, string url, object? body = null)
    {
        var msg = new HttpRequestMessage(method, url);
        msg.Headers.TryAddWithoutValidation("Authorization", AdminApiFixture.GlobalAdminAuthHeader);
        if (body is not null)
        {
            msg.Content = JsonContent.Create(body);
        }

        return msg;
    }

    private async Task<Guid> InsertConnectionWithDirectionAsync(
        string key,
        string direction,
        Guid? tenantId = null,
        string[]? authenticationSchemes = null)
    {
        Guid integrationId = Guid.NewGuid();
        Guid connectionId = Guid.NewGuid();

        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();

        await using (var integrationCmd = new NpgsqlCommand(
            """
            INSERT INTO integrations (
                id, key, contract_version, manifest_schema_version, name, direction,
                supported_auth_schemes, status, description, manifest, created_at, updated_at)
            VALUES (
                @Id, @Key, 1, 1, @Name, @Direction,
                @SupportedAuthSchemes::jsonb, 'active', 'test integration', @Manifest::jsonb, now(), now());
            """,
            connection))
        {
            integrationCmd.Parameters.AddWithValue("Id", integrationId);
            integrationCmd.Parameters.AddWithValue("Key", key);
            integrationCmd.Parameters.AddWithValue("Name", key);
            integrationCmd.Parameters.AddWithValue("Direction", direction);
            integrationCmd.Parameters.AddWithValue("SupportedAuthSchemes", JsonSerializer.Serialize(authenticationSchemes ?? []));
            integrationCmd.Parameters.AddWithValue("Manifest", TestIntegrationManifest.Create(
                key,
                key,
                direction,
                authenticationSchemes));
            await integrationCmd.ExecuteNonQueryAsync();
        }

        await using (var connectionCmd = new NpgsqlCommand(
            """
            INSERT INTO connections (id, tenant_id, integration_id, name, config, source_verification, destination_authentication, status, environment, description, created_at, updated_at)
            VALUES (@Id, @TenantId, @IntegrationId, @Name, '{"base_uri":"http://localhost:5054/sink/custom"}'::jsonb, NULL, NULL, 'active', NULL, NULL, now(), now());
            """,
            connection))
        {
            connectionCmd.Parameters.AddWithValue("Id", connectionId);
            connectionCmd.Parameters.AddWithValue("TenantId", tenantId ?? fixture.TenantId);
            connectionCmd.Parameters.AddWithValue("IntegrationId", integrationId);
            connectionCmd.Parameters.AddWithValue("Name", key);
            await connectionCmd.ExecuteNonQueryAsync();
        }

        return connectionId;
    }

    private sealed record SubscriptionDto(
        Guid Id,
        Guid TopicId,
        Guid TenantId,
        string Name,
        JsonElement MatchRules,
        Guid DestinationConnectionId,
        JsonElement? TransformConfig,
        string Status,
        int OrderIndex,
        string? Description,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt);
}
