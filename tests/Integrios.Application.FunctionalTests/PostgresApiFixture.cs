extern alias IngestionHost;

using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using Integrios.Infrastructure.Data;
using Integrios.Tests.Shared;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Respawn;

namespace Integrios.Application.FunctionalTests;

// Retains its historical name to avoid a path-only churn across the test suite. The selected
// runtime provider is owned by FunctionalDatabase.
public sealed class PostgresApiFixture : IAsyncLifetime
{
    private static readonly Guid SourceConnectorId = Guid.Parse("00000000-0000-0000-0000-000000000101");
    public const string TenantAToken = "intg_aa11bb22cc33dd440011223344556677001122334455667700112233445566";
    public const string TenantBToken = "intg_ee55ff66aa77bb888877665544332211887766554433221188776655443322";

    private readonly FunctionalDatabase database = new();
    private Respawner respawner = null!;

    public WebApplicationFactory<IngestionHost::Program> WebFactory { get; private set; } = null!;
    public string ConnectionString => database.ConnectionString;
    internal DbConnection CreateConnection() => database.CreateConnection();
    internal DbContextOptions<IntegriosDbContext> CreateOptions() => database.CreateOptions();
    public Guid TenantAId { get; private set; }
    public Guid TenantBId { get; private set; }

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

    public async Task ResetDataAsync()
    {
        await using DbConnection connection = database.CreateConnection();
        await connection.OpenAsync();
        await respawner.ResetAsync(connection);

        TenantAId = Guid.NewGuid();
        TenantBId = Guid.NewGuid();
        var credentialAId = Guid.NewGuid();
        var credentialBId = Guid.NewGuid();
        string secretHashA = Hash(TenantAToken);
        string secretHashB = Hash(TenantBToken);
        string now = database.Now;

        await connection.ExecuteAsync($$$"""
            INSERT INTO tenants (id, slug, name, status, created_at, updated_at)
            VALUES
                (@TenantAId, 'test-tenant-a', 'Test Tenant A', 'active', {{{now}}}, {{{now}}}),
                (@TenantBId, 'test-tenant-b', 'Test Tenant B', 'active', {{{now}}}, {{{now}}});

            INSERT INTO tenant_api_keys (id, tenant_id, name, key_prefix, key_hash, status, created_at)
            VALUES
                (@CredentialAId, @TenantAId, 'test-ingest-key-a', @KeyPrefixA, @KeyHashA, 'active', {{{now}}}),
                (@CredentialBId, @TenantBId, 'test-ingest-key-b', @KeyPrefixB, @KeyHashB, 'active', {{{now}}});

            INSERT INTO connectors (
                id, {{{database.KeyColumn}}}, contract_version, manifest_schema_version, name, direction,
                status, manifest, created_at, updated_at)
            VALUES (@SourceConnectorId, 'test_source', 1, 1, 'Test Source', 'source',
                'active', {{{database.Json("@SourceManifest")}}}, {{{now}}}, {{{now}}});
            """, new
            {
                TenantAId,
                TenantBId,
                CredentialAId = credentialAId,
                CredentialBId = credentialBId,
                KeyPrefixA = TenantAToken[..12],
                KeyPrefixB = TenantBToken[..12],
                KeyHashA = secretHashA,
                KeyHashB = secretHashB,
                SourceConnectorId,
                SourceManifest = TestConnectorManifest.Create(
                    "test_source", "Test Source", "source",
                    declarativeSourceContract: true,
                    sourceMappingExpression:
                        "{ \"event_type\": event_type, \"source_event_id\": source_event_id, \"payload\": payload, \"metadata\": metadata }")
            });
    }

    public Task<Guid?> GetEventTenantIdAsync(Guid eventId) =>
        ScalarAsync<Guid?>("SELECT tenant_id FROM events WHERE id=@Id", new { Id = eventId });

    public Task<Guid?> GetEventTopicIdAsync(Guid eventId) =>
        ScalarAsync<Guid?>("SELECT topic_id FROM events WHERE id=@Id", new { Id = eventId });

    public async Task<Guid> SeedTopicAsync(Guid tenantId, string name)
    {
        Guid topicId = Guid.NewGuid();
        await ExecuteAsync($"INSERT INTO topics (id,tenant_id,name,status,created_at,updated_at) VALUES (@Id,@TenantId,@Name,'active',{database.Now},{database.Now})",
            new { Id = topicId, TenantId = tenantId, Name = name });
        return topicId;
    }

    public async Task<Guid> SeedSourceConnectionAsync(
        Guid tenantId, string name, string status = "active", string direction = "source")
    {
        Guid connectorId = direction == "source" ? SourceConnectorId : Guid.NewGuid();
        if (connectorId != SourceConnectorId)
        {
            string key = $"test_{direction}_{connectorId:N}";
            await ExecuteAsync($$$"""
                INSERT INTO connectors (id,{{{database.KeyColumn}}},contract_version,manifest_schema_version,
                    name,direction,status,manifest,created_at,updated_at)
                VALUES (@Id,@Key,1,1,@Key,@Direction,'active',
                    {{{database.Json("@Manifest")}}},{{{database.Now}}},{{{database.Now}}})
                """, new
                {
                    Id = connectorId, Key = key, Direction = direction,
                    Manifest = TestConnectorManifest.Create(key, key, direction)
                });
        }

        Guid connectionId = Guid.NewGuid();
        await ExecuteAsync($$$"""
            INSERT INTO connections (id,tenant_id,connector_id,name,config,status,created_at,updated_at)
            VALUES (@Id,@TenantId,@ConnectorId,@Name,{{{database.Json("@Config")}}},@Status,{{{database.Now}}},{{{database.Now}}})
            """, new
            {
                Id = connectionId, TenantId = tenantId, ConnectorId = connectorId,
                Name = name, Config = "{}", Status = status
            });
        return connectionId;
    }

    // Seeds a Connection bound to a purpose-built Connector manifest (schema/mapping shape the
    // caller controls), for scenarios SeedSourceConnectionAsync's fixed test manifest can't cover.
    public async Task<Guid> SeedConnectorConnectionAsync(Guid tenantId, string connectorKey, string manifestJson)
    {
        Guid connectorId = Guid.NewGuid();
        await ExecuteAsync($$$"""
            INSERT INTO connectors (id,{{{database.KeyColumn}}},contract_version,manifest_schema_version,
                name,direction,status,manifest,created_at,updated_at)
            VALUES (@Id,@Key,1,1,@Key,'source','active',
                {{{database.Json("@Manifest")}}},{{{database.Now}}},{{{database.Now}}})
            """, new { Id = connectorId, Key = connectorKey, Manifest = manifestJson });

        Guid connectionId = Guid.NewGuid();
        await ExecuteAsync($$$"""
            INSERT INTO connections (id,tenant_id,connector_id,name,config,status,created_at,updated_at)
            VALUES (@Id,@TenantId,@ConnectorId,@Name,{{{database.Json("@Config")}}},'active',{{{database.Now}}},{{{database.Now}}})
            """, new
            {
                Id = connectionId, TenantId = tenantId, ConnectorId = connectorId,
                Name = connectorKey, Config = "{}"
            });
        return connectionId;
    }

    public async Task<Guid> CreateEventApiSourceAsync(
        Guid tenantId, Guid connectionId, Guid topicId,
        string configuration = """{"source_contract":"event_json"}""")
    {
        Guid sourceId = Guid.NewGuid();
        await ExecuteAsync(
            $"INSERT INTO sources (id, tenant_id, connection_id, topic_id, type, configuration, status) VALUES (@SourceId, @TenantId, @ConnectionId, @TopicId, 'event_api', {database.Json("@Configuration")}, 'active')",
            new { SourceId = sourceId, TenantId = tenantId, ConnectionId = connectionId, TopicId = topicId, Configuration = configuration });
        return sourceId;
    }

    public Task<Guid?> GetEventSourceIdAsync(Guid eventId) =>
        ScalarAsync<Guid?>("SELECT source_id FROM events WHERE id=@Id", new { Id = eventId });

    public Task ForceEventStatusAsync(Guid eventId, string status) =>
        ExecuteAsync("UPDATE events SET status=@Status WHERE id=@Id", new { Status = status, Id = eventId });

    public async Task ForceDeadLetteredDeliveryAsync(Guid eventId)
    {
        Guid tenantId = await ScalarAsync<Guid>("SELECT tenant_id FROM events WHERE id=@EventId", new { EventId = eventId });
        Guid connectionId = Guid.NewGuid();
        Guid topicId = Guid.NewGuid();
        Guid subscriptionId = Guid.NewGuid();
        await ExecuteAsync($$$"""
            INSERT INTO connectors (id,{{{database.KeyColumn}}},contract_version,manifest_schema_version,name,direction,
                status,manifest)
            VALUES (@ConnectorId,'http',1,1,'HTTP','both','active',{{{database.Json("@Manifest")}}});
            INSERT INTO connections (id,tenant_id,connector_id,name,config,status)
            VALUES (@ConnectionId,@TenantId,@ConnectorId,'replay-test-sink',{{{database.Json("@Config")}}},'active');
            INSERT INTO topics (id,tenant_id,name,status) VALUES (@TopicId,@TenantId,'replay-test-topic','active');
            INSERT INTO subscriptions (id,tenant_id,topic_id,name,match_rules,destination_connection_id,order_index,status)
            VALUES (@SubscriptionId,@TenantId,@TopicId,'replay-test-sub',{{{database.Json("@MatchRules")}}},@ConnectionId,0,'active');
            INSERT INTO event_deliveries
                (event_id,subscription_id,destination_connection_id,http_execution_snapshot,connector_key,
                 status,lifetime_attempt_count,retry_cycle_attempt_count,failed_at)
            VALUES (@EventId,@SubscriptionId,@ConnectionId,{{{database.Json("@Snapshot")}}},'http',
                'dead_lettered',3,3,{{{database.Now}}});
            """, new
            {
                ConnectorId = Guid.Parse("00000000-0000-0000-0000-000000000001"),
                Manifest = TestConnectorManifest.Create("http", "HTTP", "both"),
                ConnectionId = connectionId, TenantId = tenantId, Config = "{\"base_uri\":\"http://test/sink\"}",
                TopicId = topicId, SubscriptionId = subscriptionId,
                MatchRules = "{\"event_types\":[\"payment.created\"]}",
                Snapshot = "{\"version\":1,\"base_uri\":\"http://test/sink\",\"request\":{\"version\":1,\"method\":\"POST\",\"headers\":{},\"body\":\"json\"}}",
                EventId = eventId
            });
    }

    public Task<int> GetOutboxRowCountAsync(Guid eventId) =>
        ScalarAsync<int>("SELECT COUNT(*) FROM outbox WHERE event_id=@Id", new { Id = eventId });

    public Task<int> GetEventCountAsync() => ScalarAsync<int>("SELECT COUNT(*) FROM events");
    public Task<int> GetOutboxCountAsync() => ScalarAsync<int>("SELECT COUNT(*) FROM outbox");
    public Task<string?> GetDeliveryStatusAsync(Guid eventId) =>
        ScalarAsync<string?>("SELECT status FROM event_deliveries WHERE event_id=@Id", new { Id = eventId });

    private async Task ExecuteAsync(string sql, object? parameters = null)
    {
        await using DbConnection connection = database.CreateConnection();
        await connection.OpenAsync();
        await connection.ExecuteAsync(sql, parameters);
    }

    private async Task<T> ScalarAsync<T>(string sql, object? parameters = null)
    {
        await using DbConnection connection = database.CreateConnection();
        await connection.OpenAsync();
        return (await connection.ExecuteScalarAsync<T>(sql, parameters))!;
    }

    private WebApplicationFactory<IngestionHost::Program> BuildWebFactory() =>
        new WebApplicationFactory<IngestionHost::Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Integrios:SourceSecrets:Provider", "configuration");
            builder.UseSetting("Database:Provider", database.Provider);
            builder.UseSetting($"ConnectionStrings:{database.ConnectionName}", database.ConnectionString);
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddConfiguration(database.Configuration));
        });

    private static string Hash(string token) => "sha256:" + Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
}
