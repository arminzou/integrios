using System.Data.Common;
using System.Text.Json;
using Dapper;
using Integrios.Application.Events;
using Integrios.Application.Connectors;
using Integrios.Domain.Entities;
using Integrios.Domain.Enums;
using Integrios.Domain.ValueObjects;
using Integrios.Infrastructure.Data;
using Integrios.Infrastructure.Events;
using Integrios.Infrastructure.Connectors;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Respawn;

namespace Integrios.Application.FunctionalTests.Infrastructure;

public sealed class DatabaseProviderContractTests(DatabaseProviderFixture fixture)
    : IClassFixture<DatabaseProviderFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Baseline_UsesNativeJsonStorageAndKeepsRuntimeTriggers()
    {
        await using DbConnection connection = await fixture.OpenAsync();
        Assert.Equal(fixture.ExpectedJsonStorageTypes, await fixture.GetJsonStorageTypesAsync(connection));
        Assert.Equal(2, await fixture.GetRuntimeTriggerCountAsync(connection));
    }

    [Fact]
    public async Task JsonColumns_RejectNonObjectDocumentsAndMalformedText()
    {
        await using DbConnection connection = await fixture.OpenAsync();
        ProviderContractSeed seed = await fixture.SeedAsync(connection);
        await Assert.ThrowsAnyAsync<DbException>(() => fixture.InsertInvalidManifestAsync(connection));
        await Assert.ThrowsAnyAsync<DbException>(() => fixture.SetInvalidSourceVerificationAsync(connection, seed.ConnectionId));
        await Assert.ThrowsAnyAsync<DbException>(() => fixture.SetMalformedConfigAsync(connection, seed.ConnectionId));
    }

    [Fact]
    public async Task ScalarJsonPayload_RoundTripsThroughStorage()
    {
        await using DbConnection connection = await fixture.OpenAsync();
        ProviderContractSeed seed = await fixture.SeedAsync(connection);

        EventAcceptance scalar = await fixture.Acceptance().AcceptAsync(
            DatabaseProviderFixture.Submission(seed, "scalar-json") with
            {
                Payload = DatabaseProviderFixture.Json("42"),
            },
            null,
            CancellationToken.None);

        Assert.Equal("42", await connection.ExecuteScalarAsync<string>(
            $"SELECT {fixture.Database.JsonText("payload")} FROM events WHERE id=@EventId",
            new { scalar.EventId }));
    }

    [Fact]
    public async Task ConcurrentAcceptanceOfOneIdempotencyKey_AcceptsExactlyOnce()
    {
        await using DbConnection connection = await fixture.OpenAsync();
        ProviderContractSeed seed = await fixture.SeedAsync(connection);
        IEventAcceptance acceptance = fixture.Acceptance();
        EventSubmission submission = DatabaseProviderFixture.Submission(seed, "provider-idempotency");

        EventAcceptance[] accepted = await Task.WhenAll(
            acceptance.AcceptAsync(submission, null, CancellationToken.None),
            acceptance.AcceptAsync(submission, null, CancellationToken.None));

        Assert.Single(accepted, result => result.AlreadyAccepted);
        Assert.Single(accepted, result => !result.AlreadyAccepted);
    }

    [Fact]
    public async Task RetiredTopicSource_RejectsAcceptance()
    {
        await using DbConnection connection = await fixture.OpenAsync();
        ProviderContractSeed seed = await fixture.SeedAsync(connection);
        await connection.ExecuteAsync(
            $"UPDATE topic_sources SET status='inactive', inactive_at={fixture.Database.Now} " +
            "WHERE tenant_id=@TenantId AND topic_id=@TopicId AND connection_id=@ConnectionId",
            new { seed.TenantId, seed.TopicId, seed.ConnectionId });

        await Assert.ThrowsAsync<EventAcceptanceException>(() => fixture.Acceptance().AcceptAsync(
            DatabaseProviderFixture.Submission(seed, "retired-source"), null, CancellationToken.None));
    }

    [Fact]
    public async Task ConnectorFunctionalUpdate_IsRejected_WhilePresentationStillReconciles()
    {
        await using DbConnection connection = await fixture.OpenAsync();
        ProviderContractSeed seed = await fixture.SeedAsync(connection);
        await Assert.ThrowsAnyAsync<DbException>(() => connection.ExecuteAsync(
            "UPDATE connectors SET direction='destination' WHERE id=@ConnectorId",
            new { seed.ConnectorId }));

        await using IntegriosDbContext context = fixture.CreateContext();
        ConnectorManifest renamed = ConnectorManifestParser.DeserializeStored(
            DatabaseProviderFixture.Manifest("Renamed Source").GetRawText());
        ConnectorManifestStoreResult result = await fixture.ManifestStore(context).ApplyAsync(
            renamed, ConnectorManifestApplyAuthority.Operator, CancellationToken.None);

        Assert.Equal(ConnectorManifestApplyOutcome.PresentationReconciled, result.Outcome);
        Assert.Equal("Renamed Source", result.Connector.Name);
    }
}

public sealed class DatabaseProviderFixture : IAsyncLifetime
{
    private Respawner respawner = null!;

    internal FunctionalDatabase Database { get; } = new();
    internal string[] ExpectedJsonStorageTypes => Database.Provider == "postgres"
        ? ["jsonb", "jsonb"]
        : ["nvarchar", "nvarchar"];

    public async Task InitializeAsync()
    {
        await Database.StartAsync();
        respawner = await Database.CreateRespawnerAsync();
    }

    public async Task DisposeAsync() => await Database.DisposeAsync();

    public async Task ResetAsync()
    {
        await using DbConnection connection = await OpenAsync();
        await respawner.ResetAsync(connection);
    }

    internal async Task<DbConnection> OpenAsync()
    {
        DbConnection connection = Database.CreateConnection();
        await connection.OpenAsync();
        return connection;
    }

    internal IntegriosDbContext CreateContext() => new(Database.CreateOptions());

    internal IEventAcceptance Acceptance()
    {
        var factory = new PooledDbContextFactory<IntegriosDbContext>(Database.CreateOptions());
        return Database.Provider == "sqlserver"
            ? new SqlServerEventAcceptance(factory)
            : new PostgresEventAcceptance(factory);
    }

    internal IConnectorManifestStore ManifestStore(IntegriosDbContext context) =>
        Database.Provider == "sqlserver"
            ? new SqlServerConnectorManifestStore(context)
            : new PostgresConnectorManifestStore(context);

    internal async Task<string[]> GetJsonStorageTypesAsync(DbConnection connection)
    {
        string sql = Database.Provider == "postgres"
            ? """
              SELECT data_type FROM information_schema.columns
              WHERE table_schema='public' AND table_name='connectors' AND column_name='manifest'
              UNION ALL
              SELECT data_type FROM information_schema.columns
              WHERE table_schema='public' AND table_name='events' AND column_name='payload'
              """
            : """
              SELECT TYPE_NAME(c.user_type_id) FROM sys.columns c
              JOIN sys.tables t ON t.object_id=c.object_id
              WHERE t.name=N'connectors' AND c.name=N'manifest'
              UNION ALL
              SELECT TYPE_NAME(c.user_type_id) FROM sys.columns c
              JOIN sys.tables t ON t.object_id=c.object_id
              WHERE t.name=N'events' AND c.name=N'payload'
              """;
        return (await connection.QueryAsync<string>(sql)).ToArray();
    }

    internal Task<int> GetRuntimeTriggerCountAsync(DbConnection connection) =>
        connection.ExecuteScalarAsync<int>(Database.Provider == "postgres"
            ? "SELECT COUNT(*) FROM pg_trigger WHERE tgname IN ('connectors_reject_functional_update', 'trg_events_require_active_topic_source')"
            : "SELECT COUNT(*) FROM sys.triggers WHERE name IN (N'connectors_reject_functional_update', N'events_require_active_topic_source')");

    internal Task InsertInvalidManifestAsync(DbConnection connection) => connection.ExecuteAsync(
        Database.Provider == "postgres"
            ? """
              INSERT INTO connectors (id, key, contract_version, manifest_schema_version, name, direction,
                  supported_auth_schemes, status, created_at, updated_at, manifest)
              VALUES (gen_random_uuid(), 'bad_json', 1, 1, 'Bad JSON', 'source', '[]'::jsonb, 'active',
                  now(), now(), '[]'::jsonb)
              """
            : """
              INSERT INTO connectors (id, [key], contract_version, manifest_schema_version, name, direction,
                  supported_auth_schemes, status, created_at, updated_at, manifest)
              VALUES (NEWID(), N'bad_json', 1, 1, N'Bad JSON', N'source', N'[]', N'active',
                  SYSUTCDATETIME(), SYSUTCDATETIME(), N'[]')
              """);

    internal Task SetInvalidSourceVerificationAsync(DbConnection connection, Guid connectionId) =>
        connection.ExecuteAsync(
            Database.Provider == "postgres"
                ? "UPDATE connections SET source_verification='[]'::jsonb WHERE id=@ConnectionId"
                : "UPDATE connections SET source_verification=N'[]' WHERE id=@ConnectionId",
            new { ConnectionId = connectionId });

    internal Task SetMalformedConfigAsync(DbConnection connection, Guid connectionId) =>
        connection.ExecuteAsync(
            Database.Provider == "postgres"
                ? "UPDATE connections SET config='not-json'::jsonb WHERE id=@ConnectionId"
                : "UPDATE connections SET config=N'not-json' WHERE id=@ConnectionId",
            new { ConnectionId = connectionId });

    internal async Task<ProviderContractSeed> SeedAsync(DbConnection connection)
    {
        var seed = new ProviderContractSeed(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        string now = Database.Now;

        await connection.ExecuteAsync(
            $"INSERT INTO tenants (id, slug, name, status, created_at, updated_at) " +
            $"VALUES (@TenantId, 'provider-contract', 'Provider Contract', 'active', {now}, {now})",
            new { seed.TenantId });
        await connection.ExecuteAsync($$$"""
            INSERT INTO connectors (id, {{{Database.KeyColumn}}}, contract_version, manifest_schema_version, name, direction,
                supported_auth_schemes, status, created_at, updated_at, manifest)
            VALUES (@ConnectorId, 'test_http', 1, 1, 'Provider Contract Source', 'both',
                {{{Database.Json("@Schemes")}}}, 'active', {{{now}}}, {{{now}}}, {{{Database.Json("@ManifestJson")}}})
            """, new
        {
            seed.ConnectorId,
            Schemes = "[]",
            ManifestJson = Manifest("Provider Contract Source").GetRawText()
        });
        await connection.ExecuteAsync($$$"""
            INSERT INTO connections (id, tenant_id, connector_id, name, config, status, created_at, updated_at)
            VALUES (@ConnectionId, @TenantId, @ConnectorId, 'provider-contract-connection',
                {{{Database.Json("@Config")}}}, 'active', {{{now}}}, {{{now}}})
            """, new
        {
            seed.ConnectionId,
            seed.TenantId,
            seed.ConnectorId,
            Config = """{"base_uri":"https://example.test"}"""
        });

        await using (IntegriosDbContext context = CreateContext())
        {
            var repository = new Integrios.Infrastructure.Topics.TopicRepository(context);
            var topic = await repository.CreateAsync(
                seed.TenantId, "payments", null, [seed.ConnectionId], CancellationToken.None);
            seed = seed with { TopicId = topic.Id };
            Assert.Single(topic.Sources);
            Assert.NotNull(topic.Sources[0].Endpoint);
        }

        await connection.ExecuteAsync($$$"""
            INSERT INTO subscriptions (id, topic_id, tenant_id, name, match_rules, destination_connection_id,
                http_delivery, status, order_index, created_at, updated_at)
            VALUES (@SubscriptionId, @TopicId, @TenantId, 'payments-http', {{{Database.Json("@MatchRules")}}},
                @ConnectionId, {{{Database.Json("@HttpDelivery")}}}, 'active', 0, {{{now}}}, {{{now}}})
            """, new
        {
            seed.SubscriptionId,
            seed.TopicId,
            seed.TenantId,
            seed.ConnectionId,
            MatchRules = """{"event_type":"payment.created"}""",
            HttpDelivery = """{"body":"json","method":"POST","headers":{},"version":1}""",
        });

        return seed;
    }

    internal static EventSubmission Submission(ProviderContractSeed seed, string idempotencyKey) => new()
    {
        TenantId = seed.TenantId,
        TopicId = seed.TopicId,
        SourceConnectionId = seed.ConnectionId,
        EventType = "payment.created",
        Payload = Json("""{"amount":42}"""),
        IdempotencyKey = idempotencyKey,
    };

    internal static JsonElement Manifest(string name) => Json($$$"""
        {
          "manifest_schema_version":1,
          "key":"test_http",
          "contract_version":1,
          "direction":"both",
          "source_verification":{"allow_unverified":true,"schemes":[]},
          "destination_authentication":{"allow_unauthenticated":true,"schemes":[]},
          "source_contracts":[{"key":"verified_webhook","contract_version":1,"config":{}}],
          "presentation":{"name":"{{{name}}}","event_types":[],"authoring_presets":[]}
        }
        """);

    internal static JsonElement Json(string value) => JsonSerializer.Deserialize<JsonElement>(value);
}

internal sealed record ProviderContractSeed(
    Guid TenantId,
    Guid ConnectorId,
    Guid ConnectionId,
    Guid TopicId,
    Guid SubscriptionId);
