using System.Security.Cryptography;
using System.Text;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Integrios.Infrastructure.IntegrationTests;

public sealed class PostgresApiFixture : IAsyncLifetime
{
    private static readonly Guid SourceIntegrationId = Guid.Parse("00000000-0000-0000-0000-000000000101");
    public const string TenantAToken = "intg_aa11bb22cc33dd440011223344556677001122334455667700112233445566";
    public const string TenantBToken = "intg_ee55ff66aa77bb888877665544332211887766554433221188776655443322";

    private readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgres:16.14-alpine3.24")
        .WithDatabase("integrios")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .Build();

    public string ConnectionString => container.GetConnectionString();
    public Guid TenantAId { get; private set; }
    public Guid TenantBId { get; private set; }

    public async Task InitializeAsync()
    {
        await container.StartAsync();
        await InitializeSchemaAsync();
    }

    public async Task DisposeAsync() => await container.DisposeAsync();

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
        TenantBId = Guid.NewGuid();
        var tenantBId = TenantBId;
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
                status,
                created_at
            )
            VALUES (
                @CredentialAId,
                @TenantAId,
                'test-ingest-key-a',
                @KeyPrefixA,
                @KeyHashA,
                'active',
                now()
            ),
            (
                @CredentialBId,
                @TenantBId,
                'test-ingest-key-b',
                @KeyPrefixB,
                @KeyHashB,
                'active',
                now()
            );

            INSERT INTO integrations (
                id, key, contract_version, manifest_schema_version, name, direction,
                supported_auth_schemes, status, manifest, created_at, updated_at
            ) VALUES (
                @SourceIntegrationId, 'test_source', 1, 1, 'Test Source', 'source',
                '[]'::jsonb, 'active', @SourceManifest::jsonb, now(), now()
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
        seedCommand.Parameters.AddWithValue("SourceIntegrationId", SourceIntegrationId);
        seedCommand.Parameters.AddWithValue("SourceManifest", TestIntegrationManifest.Create(
            "test_source", "Test Source", "source"));
        await seedCommand.ExecuteNonQueryAsync();
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

    public async Task<Guid> SeedSourceConnectionAsync(
        Guid tenantId,
        string name,
        string status = "active",
        string direction = "source")
    {
        var integrationId = SourceIntegrationId;
        if (direction != "source")
        {
            integrationId = Guid.NewGuid();
        }

        var connectionId = Guid.NewGuid();
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();

        if (integrationId != SourceIntegrationId)
        {
            await using var integrationCommand = new NpgsqlCommand(
                """
                INSERT INTO integrations (
                    id, key, contract_version, manifest_schema_version, name, direction,
                    supported_auth_schemes, status, manifest, created_at, updated_at
                ) VALUES (@Id, @Key, 1, 1, @Key, @Direction, '[]'::jsonb, 'active', @Manifest::jsonb, now(), now())
                """,
                connection);
            integrationCommand.Parameters.AddWithValue("Id", integrationId);
            string key = $"test_{direction}_{integrationId:N}";
            integrationCommand.Parameters.AddWithValue("Key", key);
            integrationCommand.Parameters.AddWithValue("Direction", direction);
            integrationCommand.Parameters.AddWithValue("Manifest", TestIntegrationManifest.Create(key, key, direction));
            await integrationCommand.ExecuteNonQueryAsync();
        }

        await using var command = new NpgsqlCommand(
            """
            INSERT INTO connections (
                id, tenant_id, integration_id, name, config, status, created_at, updated_at
            ) VALUES (@Id, @TenantId, @IntegrationId, @Name, '{}'::jsonb, @Status, now(), now())
            """,
            connection);
        command.Parameters.AddWithValue("Id", connectionId);
        command.Parameters.AddWithValue("TenantId", tenantId);
        command.Parameters.AddWithValue("IntegrationId", integrationId);
        command.Parameters.AddWithValue("Name", name);
        command.Parameters.AddWithValue("Status", status);
        await command.ExecuteNonQueryAsync();
        return connectionId;
    }

    public async Task AssociateSourceAsync(Guid tenantId, Guid topicId, Guid connectionId)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO topic_sources (tenant_id, topic_id, connection_id)
            VALUES (@TenantId, @TopicId, @ConnectionId)
            """,
            connection);
        command.Parameters.AddWithValue("TenantId", tenantId);
        command.Parameters.AddWithValue("TopicId", topicId);
        command.Parameters.AddWithValue("ConnectionId", connectionId);
        await command.ExecuteNonQueryAsync();
    }

    // Removing a source from a Topic retires the association rather than deleting it, because the
    // row is a tombstone that historical Event foreign keys still point at.
    public async Task RetireSourceAsync(Guid tenantId, Guid topicId, Guid connectionId)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            UPDATE topic_sources
            SET status = 'inactive', inactive_at = now()
            WHERE tenant_id = @TenantId
              AND topic_id = @TopicId
              AND connection_id = @ConnectionId
            """,
            connection);
        command.Parameters.AddWithValue("TenantId", tenantId);
        command.Parameters.AddWithValue("TopicId", topicId);
        command.Parameters.AddWithValue("ConnectionId", connectionId);
        await command.ExecuteNonQueryAsync();
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
