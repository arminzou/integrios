using System.Security.Cryptography;
using System.Text;
using DotNet.Testcontainers.Builders;
using Integrios.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Integrios.IntegrationTests;

public sealed class PostgresApiFixture : IAsyncLifetime
{
    public const string TenantAToken = "intg_aa11bb22cc33dd440011223344556677001122334455667700112233445566";
    public const string TenantBToken = "intg_ee55ff66aa77bb888877665544332211887766554433221188776655443322";

    private readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgres:16.14-alpine3.24")
        .WithDatabase("integrios")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .Build();

    public WebApplicationFactory<Program> WebFactory { get; private set; } = null!;
    public string ConnectionString => container.GetConnectionString();
    public Guid TenantAId { get; private set; }

    public async Task InitializeAsync()
    {
        await container.StartAsync();
        await InitializeSchemaAsync();
        WebFactory = BuildWebFactory();
    }

    public async Task DisposeAsync()
    {
        WebFactory.Dispose();
        await container.DisposeAsync();
    }

    public async Task ResetDataAsync()
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();

        const string resetSql = """
            TRUNCATE TABLE subscription_deliveries, delivery_attempts, outbox, events, subscriptions, topics, connections, api_keys, tenants, integrations RESTART IDENTITY CASCADE;
            """;
        await using (var resetCommand = new NpgsqlCommand(resetSql, connection))
        {
            await resetCommand.ExecuteNonQueryAsync();
        }

        TenantAId = Guid.NewGuid();
        var tenantAId = TenantAId;
        var tenantBId = Guid.NewGuid();
        var credentialAId = Guid.NewGuid();
        var credentialBId = Guid.NewGuid();
        var secretHashA = "sha256:" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(TenantAToken))).ToLowerInvariant();
        var secretHashB = "sha256:" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(TenantBToken))).ToLowerInvariant();

        const string seedSql = """
            INSERT INTO tenants (id, slug, name, status, created_at, updated_at)
            VALUES
                (@TenantAId, 'test-tenant-a', 'Test Tenant A', 'active', now(), now()),
                (@TenantBId, 'test-tenant-b', 'Test Tenant B', 'active', now(), now());

            INSERT INTO api_keys (
                id,
                tenant_id,
                name,
                key_prefix,
                key_hash,
                scopes,
                status,
                created_at
            )
            VALUES (
                @CredentialAId,
                @TenantAId,
                'test-ingest-key-a',
                @KeyPrefixA,
                @KeyHashA,
                ARRAY['events.write'],
                'active',
                now()
            ),
            (
                @CredentialBId,
                @TenantBId,
                'test-ingest-key-b',
                @KeyPrefixB,
                @KeyHashB,
                ARRAY['events.write'],
                'active',
                now()
            );
            """;

        await using var seedCommand = new NpgsqlCommand(seedSql, connection);
        seedCommand.Parameters.AddWithValue("TenantAId", tenantAId);
        seedCommand.Parameters.AddWithValue("TenantBId", tenantBId);
        seedCommand.Parameters.AddWithValue("CredentialAId", credentialAId);
        seedCommand.Parameters.AddWithValue("CredentialBId", credentialBId);
        seedCommand.Parameters.AddWithValue("KeyPrefixA", TenantAToken[..12]);
        seedCommand.Parameters.AddWithValue("KeyPrefixB", TenantBToken[..12]);
        seedCommand.Parameters.AddWithValue("KeyHashA", secretHashA);
        seedCommand.Parameters.AddWithValue("KeyHashB", secretHashB);
        await seedCommand.ExecuteNonQueryAsync();
    }

    public async Task<Guid?> GetEventTenantIdAsync(Guid eventId)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT tenant_id FROM events WHERE id = @Id", connection);
        cmd.Parameters.AddWithValue("Id", eventId);
        var result = await cmd.ExecuteScalarAsync();
        return result is Guid g ? g : null;
    }

    public async Task<Guid?> GetEventTopicIdAsync(Guid eventId)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT topic_id FROM events WHERE id = @Id", connection);
        cmd.Parameters.AddWithValue("Id", eventId);
        var result = await cmd.ExecuteScalarAsync();
        return result is Guid g ? g : null;
    }

    public async Task<Guid> SeedTopicAsync(Guid tenantId, string name)
    {
        var topicId = Guid.NewGuid();
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO topics (id, tenant_id, name, status, created_at, updated_at) VALUES (@Id, @TenantId, @Name, 'active', now(), now())",
            connection);
        cmd.Parameters.AddWithValue("Id", topicId);
        cmd.Parameters.AddWithValue("TenantId", tenantId);
        cmd.Parameters.AddWithValue("Name", name);
        await cmd.ExecuteNonQueryAsync();
        return topicId;
    }

    public async Task ForceEventStatusAsync(Guid eventId, string status)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "UPDATE events SET status = @Status WHERE id = @Id", connection);
        cmd.Parameters.AddWithValue("Status", status);
        cmd.Parameters.AddWithValue("Id", eventId);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task ForceDeadLetteredDeliveryAsync(Guid eventId)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();

        // Self-contained: seed a minimal connection + topic + subscription, then a dead_lettered delivery.
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO integrations (id, key, name, direction, status)
            VALUES ('00000000-0000-0000-0000-000000000001', 'webhook', 'Webhook', 'both', 'active')
            ON CONFLICT (id) DO NOTHING;

            WITH ev AS (SELECT tenant_id FROM events WHERE id = @EventId),
            conn_insert AS (
                INSERT INTO connections (id, tenant_id, integration_id, name, config, status)
                SELECT gen_random_uuid(), ev.tenant_id, '00000000-0000-0000-0000-000000000001',
                       'replay-test-sink', '{"url":"http://test/sink"}'::jsonb, 'active'
                FROM ev
                RETURNING id, tenant_id
            ),
            topic_insert AS (
                INSERT INTO topics (id, tenant_id, name, status)
                SELECT gen_random_uuid(), ci.tenant_id, 'replay-test-topic', 'active'
                FROM conn_insert ci
                RETURNING id
            ),
            sub_insert AS (
                INSERT INTO subscriptions (id, topic_id, name, match_rules, destination_connection_id, order_index, status)
                SELECT gen_random_uuid(), ti.id, 'replay-test-sub',
                       '{"event_types":["payment.created"]}'::jsonb,
                       ci.id, 0, 'active'
                FROM topic_insert ti, conn_insert ci
                RETURNING id, destination_connection_id
            )
            INSERT INTO subscription_deliveries
                (event_id, subscription_id, destination_connection_id, destination_url,
                 integration_key, status, attempt_count, failed_at)
            SELECT @EventId, si.id, si.destination_connection_id, 'http://test/sink',
                   'webhook', 'dead_lettered', 3, now()
            FROM sub_insert si;
            """, connection);
        cmd.Parameters.AddWithValue("EventId", eventId);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<int> GetOutboxRowCountAsync(Guid eventId)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT COUNT(*) FROM outbox WHERE event_id = @Id", connection);
        cmd.Parameters.AddWithValue("Id", eventId);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    private async Task InitializeSchemaAsync()
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();

        foreach (var migrationPath in ResolveMigrationPaths())
        {
            var migrationSql = await File.ReadAllTextAsync(migrationPath);
            await using var migrationCommand = new NpgsqlCommand(migrationSql, connection);
            await migrationCommand.ExecuteNonQueryAsync();
        }
    }

    private WebApplicationFactory<Program> BuildWebFactory()
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Postgres"] = ConnectionString
                }));

            // The app builds its data source during startup; replace DB services explicitly
            // so repositories resolve against the container connection string in integration tests.
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<NpgsqlDataSource>();
                services.RemoveAll<IDbConnectionFactory>();

                services.AddSingleton(_ =>
                {
                    var dataSourceBuilder = new NpgsqlDataSourceBuilder(ConnectionString);
                    return dataSourceBuilder.Build();
                });
                services.AddSingleton<IDbConnectionFactory, NpgsqlConnectionFactory>();
            });
        });
    }

    private static IReadOnlyList<string> ResolveMigrationPaths()
    {
        var repoRoot = Environment.GetEnvironmentVariable("INTEGRIOS_REPO_ROOT");
        if (!string.IsNullOrWhiteSpace(repoRoot))
        {
            var envMigrationDirectory = Path.Combine(repoRoot, "db", "migrations");
            if (Directory.Exists(envMigrationDirectory))
                return Directory.GetFiles(envMigrationDirectory, "*.sql")
                    .OrderBy(GetMigrationVersion)
                    .ThenBy(Path.GetFileName, StringComparer.Ordinal)
                    .ToArray();
        }

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var solutionPath = Path.Combine(directory.FullName, "Integrios.slnx");
            if (File.Exists(solutionPath))
            {
                var migrationDirectory = Path.Combine(directory.FullName, "db", "migrations");
                return Directory.GetFiles(migrationDirectory, "*.sql")
                    .OrderBy(GetMigrationVersion)
                    .ThenBy(Path.GetFileName, StringComparer.Ordinal)
                    .ToArray();
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root from test base directory.");
    }

    private static int GetMigrationVersion(string path)
    {
        var fileName = Path.GetFileName(path);
        var separator = fileName.IndexOf("__", StringComparison.Ordinal);
        if (separator <= 1)
            return int.MaxValue;

        return int.TryParse(fileName[1..separator], out var version)
            ? version
            : int.MaxValue;
    }
}
