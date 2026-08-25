using System.Data.Common;
using System.Text.Json;
using Dapper;
using Integrios.Infrastructure;
using Integrios.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Integrios.FunctionalTests.Infrastructure;

// The one migration in this repository that rewrites data rather than schema. Without a test it
// would never execute anywhere before it executed against a real deployment: no fixture creates a
// Source in the pre-transport_config shape, so a full migrate leaves its WHERE clause matching
// nothing. This walks the database to the migration before it, plants a row in the old shape, and
// migrates forward.
public sealed class QueueConfigurationMigrationTests : IAsyncLifetime
{
    private const string PreviousMigration = "DeleteRetiredTopicSourceAndSourceEndpoint";

    private readonly FunctionalDatabase database = new(migrateOnStart: false);

    public Task InitializeAsync() => database.StartAsync();

    public async Task DisposeAsync() => await database.DisposeAsync();

    [Fact]
    public async Task Migrate_LegacyQueueConfiguration_MovesFieldsIntoTransportConfig()
    {
        await MigrateToAsync(PreviousMigration);

        Guid sourceId = await SeedLegacyQueueSourceAsync();

        await MigrateToAsync(null);

        JsonElement configuration = await ReadConfigurationAsync(sourceId);

        configuration.TryGetProperty("namespace", out _).ShouldBeFalse();
        configuration.TryGetProperty("queue_name", out _).ShouldBeFalse();

        JsonElement transportConfig = configuration.GetProperty("transport_config");
        transportConfig.GetProperty("namespace").GetString().ShouldBe("legacy.servicebus.windows.net");
        transportConfig.GetProperty("queue_name").GetString().ShouldBe("legacy-queue");

        // Untouched neighbours: the migration moves two keys and nothing else.
        configuration.GetProperty("source_contract").GetString().ShouldBe("event_json");
        configuration.GetProperty("transport").GetString().ShouldBe("azure_service_bus");
        configuration.GetProperty("authentication").GetProperty("scheme").GetString()
            .ShouldBe("azure_identity");
    }

    // A Source authored after the change already carries transport_config. The migration must leave
    // it exactly as it is rather than wrapping it a second time.
    [Fact]
    public async Task Migrate_CurrentQueueConfiguration_LeavesItUnchanged()
    {
        await MigrateToAsync(PreviousMigration);

        Guid sourceId = await SeedCurrentQueueSourceAsync();

        await MigrateToAsync(null);

        JsonElement configuration = await ReadConfigurationAsync(sourceId);
        JsonElement transportConfig = configuration.GetProperty("transport_config");

        transportConfig.GetProperty("namespace").GetString().ShouldBe("current.servicebus.windows.net");
        transportConfig.GetProperty("topic_name").GetString().ShouldBe("orders");
        transportConfig.GetProperty("subscription_name").GetString().ShouldBe("integrios");
        transportConfig.TryGetProperty("transport_config", out _).ShouldBeFalse();
    }

    // Built through the production composition so the migrations assembly is resolved exactly as a
    // deployment resolves it; the fixture's own options only wire one provider's.
    private async Task MigrateToAsync(string? targetMigration)
    {
        using ServiceProvider provider = new ServiceCollection()
            .AddAdminInfrastructureServices(database.Configuration)
            .BuildServiceProvider();
        using IServiceScope scope = provider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<IntegriosDbContext>();
        IMigrator migrator = context.GetInfrastructure().GetRequiredService<IMigrator>();
        await migrator.MigrateAsync(targetMigration);
    }

    private Task<Guid> SeedLegacyQueueSourceAsync() => SeedAsync(JsonSerializer.Serialize(new
    {
        source_contract = "event_json",
        transport = "azure_service_bus",
        @namespace = "legacy.servicebus.windows.net",
        queue_name = "legacy-queue",
        authentication = new { scheme = "azure_identity" },
    }));

    private Task<Guid> SeedCurrentQueueSourceAsync() => SeedAsync(JsonSerializer.Serialize(new
    {
        source_contract = "event_json",
        transport = "azure_service_bus",
        authentication = new { scheme = "azure_identity" },
        transport_config = new
        {
            @namespace = "current.servicebus.windows.net",
            topic_name = "orders",
            subscription_name = "integrios",
        },
    }));

    private async Task<Guid> SeedAsync(string configuration)
    {
        Guid tenantId = Guid.NewGuid();
        Guid connectorId = Guid.NewGuid();
        Guid connectionId = Guid.NewGuid();
        Guid topicId = Guid.NewGuid();
        Guid sourceId = Guid.NewGuid();

        await using DbConnection connection = database.CreateConnection();
        await connection.OpenAsync();
        await connection.ExecuteAsync($$$"""
            INSERT INTO tenants (id, slug, name, status, created_at, updated_at)
            VALUES (@TenantId, @Slug, 'Migration', 'active', {{{database.Now}}}, {{{database.Now}}});

            INSERT INTO connectors (id, {{{database.KeyColumn}}}, contract_version, manifest_schema_version,
                name, direction, status, manifest, created_at, updated_at)
            VALUES (@ConnectorId, @ConnectorKey, 1, 1, 'Migration', 'source', 'active',
                {{{database.Json("@Manifest")}}}, {{{database.Now}}}, {{{database.Now}}});

            INSERT INTO connections (id, tenant_id, connector_id, name, config, status, created_at, updated_at)
            VALUES (@ConnectionId, @TenantId, @ConnectorId, 'migration', {{{database.Json("@Empty")}}},
                'active', {{{database.Now}}}, {{{database.Now}}});

            INSERT INTO topics (id, tenant_id, name, status, created_at, updated_at)
            VALUES (@TopicId, @TenantId, 'migration-topic', 'active', {{{database.Now}}}, {{{database.Now}}});

            INSERT INTO sources (id, tenant_id, connection_id, topic_id, type, configuration, status)
            VALUES (@SourceId, @TenantId, @ConnectionId, @TopicId, 'queue',
                {{{database.Json("@Configuration")}}}, 'active');
            """,
            new
            {
                TenantId = tenantId,
                Slug = $"mig-{Guid.NewGuid():N}"[..12],
                ConnectorKey = "migration",
                ConnectorId = connectorId,
                Manifest = Integrios.Tests.Shared.TestConnectorManifest.Create(
                    "migration", "Migration", "source", declarativeSourceContract: true),
                ConnectionId = connectionId,
                Empty = "{}",
                TopicId = topicId,
                SourceId = sourceId,
                Configuration = configuration,
            });

        return sourceId;
    }

    private async Task<JsonElement> ReadConfigurationAsync(Guid sourceId)
    {
        await using DbConnection connection = database.CreateConnection();
        await connection.OpenAsync();
        string json = await connection.QuerySingleAsync<string>(
            $"SELECT {database.JsonText("configuration")} FROM sources WHERE id=@Id",
            new { Id = sourceId });
        return JsonSerializer.Deserialize<JsonElement>(json);
    }
}
