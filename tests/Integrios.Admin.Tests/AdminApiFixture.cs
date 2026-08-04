using Integrios.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Integrios.Admin.Tests;

public sealed class AdminApiFixture : IAsyncLifetime
{
    public const string GlobalAdminPublicKey = "global_admin_key";
    public const string GlobalAdminSecret = "admin_bootstrap_secret";
    public const string GlobalAdminAuthHeader = $"AdminKey {GlobalAdminPublicKey}:{GlobalAdminSecret}";
    public const string InvalidAdminAuthHeader = "AdminKey legacy_tenant_key:unsupported-secret";

    private static readonly Guid HttpIntegrationId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    private readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgres:16.14-alpine3.24")
        .WithDatabase("integrios")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .Build();

    public WebApplicationFactory<Program> WebFactory { get; private set; } = null!;
    public string ConnectionString => container.GetConnectionString();
    public Guid TenantId { get; private set; }
    public Guid OtherTenantId { get; private set; }
    public Guid SourceConnectionId { get; private set; }

    public async Task InitializeAsync()
    {
        await container.StartAsync();
        await RunMigrationsAsync();
        WebFactory = BuildWebFactory();
    }

    public async Task DisposeAsync()
    {
        WebFactory.Dispose();
        await container.DisposeAsync();
    }

    public async Task ResetAsync()
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();

        // admin_keys must be listed explicitly so CASCADE doesn't surprise us,
        // and we re-seed the global bootstrap key in SeedAsync.
        await using var truncateCmd = new NpgsqlCommand(
            """
            TRUNCATE TABLE subscription_deliveries, delivery_attempts, outbox, events,
                subscriptions, source_endpoints, topic_sources, topics, connections, integrations,
                api_keys, admin_keys, tenants
            RESTART IDENTITY CASCADE;
            """, connection);
        await truncateCmd.ExecuteNonQueryAsync();

        await SeedAsync(connection);
    }

    private async Task SeedAsync(NpgsqlConnection connection)
    {
        TenantId = Guid.NewGuid();
        OtherTenantId = Guid.NewGuid();
        SourceConnectionId = Guid.NewGuid();
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO tenants (id, slug, name, status, created_at, updated_at)
            VALUES
                (@TenantId, 'test-tenant', 'Test Tenant', 'active', now(), now()),
                (@OtherTenantId, 'other-tenant', 'Other Tenant', 'active', now(), now());

            INSERT INTO integrations (
                id, key, contract_version, manifest_schema_version, name, direction,
                supported_auth_schemes, status, manifest)
            VALUES (
                @IntegrationId, 'http', 1, 1, 'HTTP', 'both',
                '["api_key_header","bearer_token"]'::jsonb, 'active', @Manifest::jsonb)
            ON CONFLICT (id) DO NOTHING;

            INSERT INTO connections (id, tenant_id, integration_id, name, config, status)
            VALUES (
                @SourceConnectionId, @TenantId, @IntegrationId, 'source',
                '{"base_uri":"http://localhost:5054/sink/source"}', 'active');

            -- Re-seed global bootstrap key (truncated in ResetAsync)
            INSERT INTO admin_keys (public_key, secret_hash, name, created_at)
            VALUES ('global_admin_key',
                    'sha256:5af35a0149f5a07231b181c3b4d5d3a76a4c765258533a123b34dfb843599328',
                    'Bootstrap Operator Admin Key', now());
            """, connection);

        cmd.Parameters.AddWithValue("TenantId", TenantId);
        cmd.Parameters.AddWithValue("OtherTenantId", OtherTenantId);
        cmd.Parameters.AddWithValue("IntegrationId", HttpIntegrationId);
        cmd.Parameters.AddWithValue("Manifest", TestIntegrationManifest.Create(
            "http",
            "HTTP",
            "both",
            authenticationSchemes: ["api_key_header", "bearer_token"],
            description: "Generic HTTP source or destination.",
            allowUnauthenticated: true));
        cmd.Parameters.AddWithValue("SourceConnectionId", SourceConnectionId);
        await cmd.ExecuteNonQueryAsync();
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

            builder.ConfigureServices(services =>
            {
                services.RemoveAll<NpgsqlDataSource>();
                services.RemoveAll<IDbConnectionFactory>();
                services.RemoveAll<PublicIngressBaseUri>();

                services.AddSingleton(_ => new NpgsqlDataSourceBuilder(ConnectionString).Build());
                services.AddSingleton<IDbConnectionFactory, NpgsqlConnectionFactory>();
                services.AddSingleton(PublicIngressBaseUri.Parse(
                    "https://ingress.example.test/proxy/integrios",
                    allowHttp: false));
            });
        });
    }

    private async Task RunMigrationsAsync()
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();

        foreach (var path in ResolveMigrationPaths())
        {
            var sql = await File.ReadAllTextAsync(path);
            await using var cmd = new NpgsqlCommand(sql, connection);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    private static IEnumerable<string> ResolveMigrationPaths()
    {
        var repoRoot = Environment.GetEnvironmentVariable("INTEGRIOS_REPO_ROOT");
        if (!string.IsNullOrWhiteSpace(repoRoot))
        {
            var envDir = Path.Combine(repoRoot, "db", "migrations");
            if (Directory.Exists(envDir))
                return MigrationFiles(envDir);
        }

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Integrios.slnx")))
                return MigrationFiles(Path.Combine(directory.FullName, "db", "migrations"));
            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root.");
    }

    private static IEnumerable<string> MigrationFiles(string dir) =>
        Directory.GetFiles(dir, "*.sql")
            .Where(p => !Path.GetFileName(p).StartsWith("V4__"))
            .OrderBy(GetMigrationVersion)
            .ThenBy(Path.GetFileName, StringComparer.Ordinal);

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
