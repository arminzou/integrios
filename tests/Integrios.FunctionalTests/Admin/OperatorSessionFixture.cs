using System.Data.Common;
using Integrios.Admin;
using Integrios.Admin.Auth;
using Integrios.Infrastructure.Hosting;
using Integrios.Tests.Shared;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Respawn;

namespace Integrios.FunctionalTests.Admin;

/// Runs the Admin host with a real identity provider configured, so the browser session path is
/// exercised end to end. Two hosts are built against two issuers to prove identities stay separate.
public sealed class OperatorSessionFixture : IAsyncLifetime
{
    private readonly FunctionalDatabase database = new();
    private readonly MockOidcProvider provider = new();
    private Respawner respawner = null!;

    internal MockOidcProvider Provider => provider;
    public WebApplicationFactory<Program> AliceHost { get; private set; } = null!;
    public WebApplicationFactory<Program> BobHost { get; private set; } = null!;

    /// A second host on the same issuer and the same Data Protection key ring, standing in for a
    /// second Admin replica behind one address.
    public WebApplicationFactory<Program> AliceReplica { get; private set; } = null!;

    public TimeSpan ConfiguredLifetime => TimeSpan.FromHours(8);

    public async Task InitializeAsync()
    {
        await database.StartAsync();
        await provider.StartAsync();
        respawner = await database.CreateRespawnerAsync();

        string keyRing = Path.Combine(Path.GetTempPath(), "integrios-session-keys-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(keyRing);

        AliceHost = BuildHost(MockOidcProvider.AliceIssuerId, keyRing);
        AliceReplica = BuildHost(MockOidcProvider.AliceIssuerId, keyRing);
        BobHost = BuildHost(MockOidcProvider.BobIssuerId, keyRing);
    }

    public async Task DisposeAsync()
    {
        AliceHost.Dispose();
        AliceReplica.Dispose();
        BobHost.Dispose();
        await provider.DisposeAsync();
        await database.DisposeAsync();
    }

    public async Task ResetAsync()
    {
        await using DbConnection connection = database.CreateConnection();
        await connection.OpenAsync();
        await respawner.ResetAsync(connection);

        // The machine credential must keep working alongside the browser session, so the same
        // OperatorKey the other Admin tests use is available here too.
        await using DbCommand seed = connection.CreateCommand();
        seed.CommandText = $"""
            INSERT INTO operator_keys (public_key, secret_hash, name, created_at)
            VALUES ('{AdminApiFixture.GlobalOperatorPublicKey}',
                    'sha256:e98f79daedd50eea3a83ba72c3cd33802bcb5432a6e6273d1fe0bf573dfe8420',
                    'Bootstrap Operator Key', {database.Now});
            """;
        await seed.ExecuteNonQueryAsync();
    }

    public async Task<int> CountAsync(string table)
    {
        await using DbConnection connection = database.CreateConnection();
        await connection.OpenAsync();
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table}";
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private WebApplicationFactory<Program> BuildHost(string issuerId, string keyRingPath) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Database:Provider", database.Provider);
            builder.UseSetting($"ConnectionStrings:{database.ConnectionName}", database.ConnectionString);
            builder.UseSetting(OperatorOidcOptions.AuthorityKey, provider.Authority(issuerId));
            builder.UseSetting(OperatorOidcOptions.SectionKey + ":ClientId", MockOidcProvider.ClientId);
            builder.UseSetting(OperatorOidcOptions.SectionKey + ":ClientSecret", MockOidcProvider.ClientSecret);
            // The containerized provider is reached over plain HTTP inside the test network only.
            builder.UseSetting(OperatorOidcOptions.SectionKey + ":RequireHttpsMetadata", "false");
            builder.UseSetting(OperatorSessionOptions.LifetimeKey, "08:00:00");
            builder.ConfigureAppConfiguration((_, config) => config.AddConfiguration(database.Configuration));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<PublicIngestionBaseUri>();
                services.AddSingleton(PublicIngestionBaseUri.Parse(
                    "https://ingestion.example.test/proxy/integrios", allowHttp: false));
                // A shared, durable key ring is what lets one replica read another's cookie.
                services.AddDataProtection()
                    .PersistKeysToFileSystem(new DirectoryInfo(keyRingPath))
                    .SetApplicationName("Integrios.Admin");
            });
        });
}
