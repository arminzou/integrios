using System.Data.Common;
using Dapper;
using Integrios.Admin;
using Integrios.Infrastructure.Data;
using Integrios.Tests.Shared;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Respawn;

namespace Integrios.Application.FunctionalTests.Admin;

public sealed class AdminApiFixture : IAsyncLifetime
{
    public const string GlobalAdminPublicKey = "global_admin_key";
    public const string GlobalAdminSecret = "admin_bootstrap_secret";
    public const string GlobalAdminAuthHeader = $"AdminKey {GlobalAdminPublicKey}:{GlobalAdminSecret}";
    public const string InvalidAdminAuthHeader = "AdminKey legacy_tenant_key:unsupported-secret";

    private static readonly Guid HttpIntegrationId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private readonly FunctionalDatabase database = new();
    private Respawner respawner = null!;

    public WebApplicationFactory<Program> WebFactory { get; private set; } = null!;
    public string ConnectionString => database.ConnectionString;
    internal string PresentationDriftExpression => database.Provider == "postgres"
        ? "jsonb_set(jsonb_set(manifest, '{presentation,name}', '\"Drifted\"'), '{presentation,description}', '\"Drifted description\"')"
        : "JSON_MODIFY(JSON_MODIFY(manifest, '$.presentation.name', 'Drifted'), '$.presentation.description', 'Drifted description')";
    internal DbConnection CreateConnection() => database.CreateConnection();
    internal string Json(string parameter) => database.Json(parameter);
    internal string JsonText(string column) => database.JsonText(column);
    internal string Now => database.Now;
    internal string KeyColumn => database.KeyColumn;
    internal IConfiguration Configuration => database.Configuration;
    public Guid TenantId { get; private set; }
    public Guid OtherTenantId { get; private set; }
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

    private async Task SeedAsync(DbConnection connection)
    {
        TenantId = Guid.NewGuid();
        OtherTenantId = Guid.NewGuid();
        SourceConnectionId = Guid.NewGuid();
        string now = database.Now;
        string json = database.Json("@Manifest");
        await connection.ExecuteAsync($$$"""
            INSERT INTO tenants (id, slug, name, status, created_at, updated_at)
            VALUES
                (@TenantId, 'test-tenant', 'Test Tenant', 'active', {{{now}}}, {{{now}}}),
                (@OtherTenantId, 'other-tenant', 'Other Tenant', 'active', {{{now}}}, {{{now}}});

            INSERT INTO integrations (
                id, {{{database.KeyColumn}}}, contract_version, manifest_schema_version, name, direction,
                supported_auth_schemes, status, manifest)
            VALUES (
                @IntegrationId, 'http', 1, 1, 'HTTP', 'both',
                {{{database.Json("@SupportedAuthSchemes")}}}, 'active', {{{json}}});

            INSERT INTO connections (id, tenant_id, integration_id, name, config, status)
            VALUES (@SourceConnectionId, @TenantId, @IntegrationId, 'source',
                {{{database.Json("@Config")}}}, 'active');

            INSERT INTO admin_keys (public_key, secret_hash, name, created_at)
            VALUES ('global_admin_key',
                    'sha256:5af35a0149f5a07231b181c3b4d5d3a76a4c765258533a123b34dfb843599328',
                    'Bootstrap Operator Admin Key', {{{now}}});
            """, new
            {
                TenantId,
                OtherTenantId,
                IntegrationId = HttpIntegrationId,
                SupportedAuthSchemes = "[\"api_key_header\",\"bearer_token\"]",
                Manifest = TestIntegrationManifest.Create(
                    "http", "HTTP", "both",
                    authenticationSchemes: ["api_key_header", "bearer_token"],
                    description: "Generic HTTP source or destination.",
                    allowUnauthenticated: true),
                SourceConnectionId,
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
                services.RemoveAll<PublicIngressBaseUri>();
                services.AddSingleton(PublicIngressBaseUri.Parse(
                    "https://ingress.example.test/proxy/integrios", allowHttp: false));
            });
        });
}
