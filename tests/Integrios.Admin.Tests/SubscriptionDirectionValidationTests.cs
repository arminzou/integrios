using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Integrios.Application.Topics;
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
                dlqEnabled = true,
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
                dlqEnabled = true,
                orderIndex = 10
            }));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
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
                dlqEnabled = false,
                orderIndex = 25
            }));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    private async Task<TopicResponse> CreateTopicAsync(string name)
    {
        var response = await client.SendAsync(AdminRequest(
            HttpMethod.Post,
            $"/admin/tenants/{fixture.TenantId}/topics",
            new { name }));

        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<TopicResponse>(WebJson))!;
    }

    private async Task<SubscriptionResponse> CreateSubscriptionAsync(Guid topicId, string name, string eventType)
    {
        var response = await client.SendAsync(AdminRequest(
            HttpMethod.Post,
            $"/admin/tenants/{fixture.TenantId}/topics/{topicId}/subscriptions",
            new
            {
                name,
                matchRules = new { event_type = eventType },
                destinationConnectionId = fixture.SourceConnectionId,
                dlqEnabled = true,
                orderIndex = 10
            }));

        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<SubscriptionResponse>(WebJson))!;
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

    private async Task<Guid> InsertConnectionWithDirectionAsync(string key, string direction)
    {
        Guid integrationId = Guid.NewGuid();
        Guid connectionId = Guid.NewGuid();

        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();

        await using (var integrationCmd = new NpgsqlCommand(
            """
            INSERT INTO integrations (id, key, name, direction, supported_auth_schemes, status, description, created_at, updated_at)
            VALUES (@Id, @Key, @Name, @Direction, '[]'::jsonb, 'active', 'test integration', now(), now());
            """,
            connection))
        {
            integrationCmd.Parameters.AddWithValue("Id", integrationId);
            integrationCmd.Parameters.AddWithValue("Key", key);
            integrationCmd.Parameters.AddWithValue("Name", key);
            integrationCmd.Parameters.AddWithValue("Direction", direction);
            await integrationCmd.ExecuteNonQueryAsync();
        }

        await using (var connectionCmd = new NpgsqlCommand(
            """
            INSERT INTO connections (id, tenant_id, integration_id, name, config, auth, status, environment, description, created_at, updated_at)
            VALUES (@Id, @TenantId, @IntegrationId, @Name, '{"url":"http://localhost:5054/sink/custom"}'::jsonb, NULL, 'active', NULL, NULL, now(), now());
            """,
            connection))
        {
            connectionCmd.Parameters.AddWithValue("Id", connectionId);
            connectionCmd.Parameters.AddWithValue("TenantId", fixture.TenantId);
            connectionCmd.Parameters.AddWithValue("IntegrationId", integrationId);
            connectionCmd.Parameters.AddWithValue("Name", key);
            await connectionCmd.ExecuteNonQueryAsync();
        }

        return connectionId;
    }

    private sealed record SubscriptionResponse(
        Guid Id,
        Guid TopicId,
        Guid TenantId,
        string Name,
        JsonElement MatchRules,
        Guid DestinationConnectionId,
        JsonElement? TransformConfig,
        bool DlqEnabled,
        string Status,
        int OrderIndex,
        string? Description,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt);
}
