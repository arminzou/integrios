using System.Data.Common;
using Integrios.Admin;
using Integrios.Admin.Auth;
using Integrios.Application;
using Integrios.Application.Identity;
using Integrios.Infrastructure;
using Integrios.Infrastructure.Hosting;
using Integrios.Tests.Shared;
using MediatR;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
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
    private readonly string keyRingPath = Path.Combine(
        Path.GetTempPath(),
        "integrios-session-keys-" + Guid.NewGuid().ToString("N"));
    private readonly string otherKeyRingPath = Path.Combine(
        Path.GetTempPath(),
        "integrios-session-keys-" + Guid.NewGuid().ToString("N"));
    private Respawner respawner = null!;

    internal MockOidcProvider Provider => provider;
    public WebApplicationFactory<Program> AliceHost { get; private set; } = null!;
    public WebApplicationFactory<Program> BobHost { get; private set; } = null!;
    public WebApplicationFactory<Program> PasswordHost { get; private set; } = null!;
    public WebApplicationFactory<Program> PasswordReplica { get; private set; } = null!;
    public WebApplicationFactory<Program> BothHost { get; private set; } = null!;

    /// A second host on the same issuer and the same Data Protection key ring, standing in for a
    /// second Admin replica behind one address.
    public WebApplicationFactory<Program> AliceReplica { get; private set; } = null!;

    /// A password host whose configured key ring differs from every other host's. Both hosts share
    /// a process and content root, so without honoring the configured path they would fall back to
    /// the same default key store and the replica tests could not tell the difference.
    public WebApplicationFactory<Program> PasswordOtherKeyRing { get; private set; } = null!;

    public TimeSpan ConfiguredLifetime => TimeSpan.FromHours(8);

    public async Task InitializeAsync()
    {
        await database.StartAsync();
        await provider.StartAsync();
        respawner = await database.CreateRespawnerAsync();

        Directory.CreateDirectory(keyRingPath);
        Directory.CreateDirectory(otherKeyRingPath);

        AliceHost = BuildHost(MockOidcProvider.AliceIssuerId, passwordEnabled: false);
        AliceReplica = BuildHost(MockOidcProvider.AliceIssuerId, passwordEnabled: false);
        BobHost = BuildHost(MockOidcProvider.BobIssuerId, passwordEnabled: false);
        PasswordHost = BuildHost(issuerId: null, passwordEnabled: true);
        PasswordReplica = BuildHost(issuerId: null, passwordEnabled: true);
        BothHost = BuildHost(MockOidcProvider.AliceIssuerId, passwordEnabled: true);
        PasswordOtherKeyRing = BuildHost(issuerId: null, passwordEnabled: true, otherKeyRingPath);
    }

    public async Task DisposeAsync()
    {
        AliceHost.Dispose();
        AliceReplica.Dispose();
        BobHost.Dispose();
        PasswordHost.Dispose();
        PasswordReplica.Dispose();
        BothHost.Dispose();
        PasswordOtherKeyRing.Dispose();
        await provider.DisposeAsync();
        await database.DisposeAsync();
        Directory.Delete(keyRingPath, recursive: true);
        Directory.Delete(otherKeyRingPath, recursive: true);
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

    public async Task<Guid> CreatePasswordUserAsync(
        string email = "password@example.com",
        string password = "correct-password!")
    {
        string hash = new PasswordHasher<string>().HashPassword(string.Empty, password);
        PasswordCredentialMutationResult result = await SendAsync(
            new CreateOperatorUserCommand("Password Operator", email, hash));
        result.Status.ShouldBe(PasswordCredentialMutationStatus.Succeeded);
        return result.UserId;
    }

    public async Task<T> SendAsync<T>(IRequest<T> request)
    {
        await using ServiceProvider services = new ServiceCollection()
            .AddAdminApplicationServices()
            .AddAdminInfrastructureServices(database.Configuration)
            .BuildServiceProvider();
        return await services.GetRequiredService<ISender>().Send(request);
    }

    private WebApplicationFactory<Program> BuildHost(
        string? issuerId,
        bool passwordEnabled,
        string? keyRing = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Database:Provider", database.Provider);
            builder.UseSetting($"ConnectionStrings:{database.ConnectionName}", database.ConnectionString);
            builder.UseSetting(AdminDataProtection.KeyRingPathKey, keyRing ?? keyRingPath);
            if (issuerId is not null)
            {
                builder.UseSetting(OperatorOidcOptions.AuthorityKey, provider.Authority(issuerId));
                builder.UseSetting(OperatorOidcOptions.SectionKey + ":ClientId", MockOidcProvider.ClientId);
                builder.UseSetting(OperatorOidcOptions.SectionKey + ":ClientSecret", MockOidcProvider.ClientSecret);
                builder.UseSetting(OperatorOidcOptions.SectionKey + ":DisplayName", "Test SSO");
            }
            // The containerized provider is reached over plain HTTP inside the test network only.
            builder.UseSetting(OperatorOidcOptions.SectionKey + ":RequireHttpsMetadata", "false");
            builder.UseSetting(OperatorPasswordOptions.EnabledKey, passwordEnabled.ToString());
            builder.UseSetting(OperatorSessionOptions.LifetimeKey, "08:00:00");
            builder.ConfigureAppConfiguration((_, config) => config.AddConfiguration(database.Configuration));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<PublicIngestionBaseUri>();
                services.AddSingleton(PublicIngestionBaseUri.Parse(
                    "https://ingestion.example.test/proxy/integrios", allowHttp: false));
            });
        });
}
