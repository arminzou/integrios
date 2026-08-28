using System.Data.Common;
using System.Net;
using System.Text;
using System.Text.Json;
using Dapper;
using Integrios.Admin;
using Integrios.Application.Bootstrap;
using Integrios.Infrastructure.Data;
using Integrios.Tests.Shared;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Respawn;

namespace Integrios.FunctionalTests.Admin;

public sealed class AdminApiFixture : IAsyncLifetime
{
    public const string GlobalOperatorPublicKey = "global_operator_key";
    public const string GlobalOperatorSecret = "operator_bootstrap_secret";
    public const string GlobalOperatorAuthHeader = $"OperatorKey {GlobalOperatorPublicKey}:{GlobalOperatorSecret}";
    public const string InvalidOperatorAuthHeader = "OperatorKey unknown_operator_key:unsupported-secret";
    public const string InvalidOperatorSecretAuthHeader = $"OperatorKey {GlobalOperatorPublicKey}:unsupported-secret";

    private readonly FunctionalDatabase database = new();
    private Respawner respawner = null!;

    public WebApplicationFactory<Program> WebFactory { get; private set; } = null!;
    public string ConnectionString => database.ConnectionString;
    internal DbConnection CreateConnection() => database.CreateConnection();
    internal string Json(string parameter) => database.Json(parameter);
    internal string JsonText(string column) => database.JsonText(column);
    internal string Now => database.Now;
    internal string KeyColumn => database.KeyColumn;
    internal IConfiguration Configuration => database.Configuration;
    public Guid TenantId { get; private set; }
    public Guid OtherTenantId { get; private set; }
    public Guid HttpConnectorId { get; private set; }
    public Guid SourceConnectionId { get; private set; }

    public async Task InitializeAsync()
    {
        await database.StartAsync();
        respawner = await database.CreateRespawnerAsync();
        WebFactory = BuildWebFactory();
    }

    public async Task DisposeAsync()
    {
        WebFactory.Dispose();
        await database.DisposeAsync();
    }

    public async Task ResetAsync()
    {
        await using DbConnection connection = database.CreateConnection();
        await connection.OpenAsync();
        await respawner.ResetAsync(connection);
        await SeedAsync(connection);
    }

    public async Task<(Guid EventId, Guid DeliveryId)> SeedDeadLetteredDeliveryAsync()
    {
        Guid topicId = Guid.NewGuid();
        Guid destinationConnectionId = Guid.NewGuid();
        Guid subscriptionId = Guid.NewGuid();
        Guid eventId = Guid.NewGuid();
        Guid sourceId = Guid.NewGuid();
        Guid deliveryId = Guid.NewGuid();
        Guid attemptId = Guid.NewGuid();

        await using DbConnection connection = database.CreateConnection();
        await connection.OpenAsync();
        await connection.ExecuteAsync($$$"""
            INSERT INTO connections (id, tenant_id, connector_id, name, config, status)
            VALUES (@DestinationConnectionId, @TenantId, @ConnectorId, @DestinationName,
                {{{database.Json("@DestinationConfig")}}}, 'active');
            INSERT INTO topics (id, tenant_id, name, status) VALUES (@TopicId, @TenantId, @TopicName, 'active');
            INSERT INTO sources (id, tenant_id, connection_id, topic_id, type, configuration, status)
            VALUES (@SourceId, @TenantId, @SourceConnectionId, @TopicId, 'event_api', {{{database.Json("@SourceConfig")}}}, 'active');
            INSERT INTO subscriptions (id, tenant_id, topic_id, name, match_rules, destination_connection_id, order_index, status)
            VALUES (@SubscriptionId, @TenantId, @TopicId, @SubscriptionName,
                {{{database.Json("@MatchRules")}}}, @DestinationConnectionId, 0, 'active');
            INSERT INTO events (id, tenant_id, topic_id, source_id, event_type, payload, status)
            VALUES (@EventId, @TenantId, @TopicId, @SourceId, 'recovery.test',
                {{{database.Json("@Payload")}}}, 'routed');
            INSERT INTO event_deliveries
                (id, event_id, subscription_id, destination_connection_id, http_execution_snapshot, connector_key,
                 status, lifetime_attempt_count, retry_cycle_attempt_count, failed_at)
            VALUES (@DeliveryId, @EventId, @SubscriptionId, @DestinationConnectionId,
                {{{database.Json("@Snapshot")}}}, 'http', 'dead_lettered', 1, 1, {{{database.Now}}});
            INSERT INTO delivery_attempts
                (id, event_delivery_id, attempt_number, status, failure_phase, started_at, completed_at)
            VALUES (@AttemptId, @DeliveryId, 1, 'failed', 'http', {{{database.Now}}}, {{{database.Now}}});
            """, new
        {
            TenantId,
            SourceConnectionId,
            SourceId = sourceId,
            ConnectorId = HttpConnectorId,
            DestinationConnectionId = destinationConnectionId,
            TopicId = topicId,
            SubscriptionId = subscriptionId,
            EventId = eventId,
            DeliveryId = deliveryId,
            DestinationName = $"recovery-destination-{destinationConnectionId:N}",
            TopicName = $"recovery-topic-{topicId:N}",
            SubscriptionName = $"recovery-subscription-{subscriptionId:N}",
            AttemptId = attemptId,
            DestinationConfig = "{\"base_uri\":\"http://localhost:5054/sink/recovery\"}",
            MatchRules = "{\"event_types\":[\"recovery.test\"]}",
            Payload = "{\"recovery\":true}",
            SourceConfig = "{}",
            Snapshot = "{\"version\":1,\"base_uri\":\"http://localhost:5054/sink/recovery\",\"request\":{\"version\":1,\"method\":\"POST\",\"headers\":{},\"body\":\"json\"}}"
        });

        return (eventId, deliveryId);
    }

    public async Task<Guid> ApplyConnectorManifestAsync(string key, string manifestJson)
    {
        using HttpClient client = WebFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/admin/connectors/{key}/versions/1")
        {
            Content = new StringContent(manifestJson, Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation("Authorization", GlobalOperatorAuthHeader);
        using HttpResponseMessage response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("id").GetGuid();
    }

    public async Task<string?> GetDeliveryStatusAsync(Guid deliveryId)
    {
        await using DbConnection connection = database.CreateConnection();
        await connection.OpenAsync();
        return await connection.ExecuteScalarAsync<string?>(
            "SELECT status FROM event_deliveries WHERE id = @DeliveryId",
            new { DeliveryId = deliveryId });
    }

    private async Task SeedAsync(DbConnection connection)
    {
        TenantId = Guid.NewGuid();
        OtherTenantId = Guid.NewGuid();
        SourceConnectionId = Guid.NewGuid();
        string now = database.Now;
        await connection.ExecuteAsync($$$"""
            INSERT INTO tenants (id, slug, name, status, created_at, updated_at)
            VALUES
                (@TenantId, 'test-tenant', 'Test Tenant', 'active', {{{now}}}, {{{now}}}),
                (@OtherTenantId, 'other-tenant', 'Other Tenant', 'active', {{{now}}}, {{{now}}});

            INSERT INTO operator_keys (public_key, secret_hash, name, created_at)
            VALUES ('global_operator_key',
                    'sha256:e98f79daedd50eea3a83ba72c3cd33802bcb5432a6e6273d1fe0bf573dfe8420',
                    'Bootstrap Operator Key', {{{now}}});
            """, new
            {
                TenantId,
                OtherTenantId,
            });

        HttpConnectorId = await ApplyConnectorManifestAsync(
            "http", TestConnectorManifest.Create("http", "HTTP", "both"));

        await connection.ExecuteAsync($$$"""
            INSERT INTO connections (id, tenant_id, connector_id, name, config, status)
            VALUES (@SourceConnectionId, @TenantId, @ConnectorId, 'source',
                {{{database.Json("@Config")}}}, 'active');
            """, new
            {
                SourceConnectionId,
                TenantId,
                ConnectorId = HttpConnectorId,
                Config = "{\"base_uri\":\"http://localhost:5054/sink/source\"}"
            });
    }

    private WebApplicationFactory<Program> BuildWebFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Database:Provider", database.Provider);
            builder.UseSetting($"ConnectionStrings:{database.ConnectionName}", database.ConnectionString);
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddConfiguration(database.Configuration));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<PublicIngestionBaseUri>();
                services.AddSingleton(PublicIngestionBaseUri.Parse(
                    "https://ingestion.example.test/proxy/integrios", allowHttp: false));
            });
        });
}
