using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Integrios.Application;
using Integrios.Application.Bootstrap;
using Integrios.Application.Integrations;
using Integrios.Domain.Common;
using Integrios.Domain.Integrations;
using Integrios.Infrastructure;
using MediatR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Integrios.Application.FunctionalTests.Admin;

public sealed class BootstrapTests : IClassFixture<AdminApiFixture>, IAsyncLifetime
{
    private readonly AdminApiFixture fixture;
    private ServiceProvider provider = null!;
    private IMediator mediator = null!;

    public BootstrapTests(AdminApiFixture fixture)
    {
        this.fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        await fixture.ResetAsync();

        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = fixture.ConnectionString
            })
            .Build();

        var services = new ServiceCollection();
        services.AddAdminApplicationServices();
        services.AddAdminInfrastructureServices(configuration);
        provider = services.BuildServiceProvider();
        mediator = provider.GetRequiredService<IMediator>();
    }

    public Task DisposeAsync()
    {
        provider.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task BootstrapBuiltins_IsIdempotent_AndReconcilesPresentationAndStatusDrift()
    {
        IReadOnlyList<Integration> first = await mediator.Send(new BootstrapBuiltinsCommand());
        Integration http = Assert.Single(first, i => i.Key == "http");
        Assert.Equal(BuiltinCatalog.HttpId, http.Id);
        Assert.Equal(IntegrationDirection.Both, http.Direction);
        Assert.Equal(["api_key_header", "bearer_token"], http.SupportedAuthSchemes.Order(StringComparer.Ordinal));
        JsonElement destinationSchema = http.Manifest.DestinationConfigurationSchema!.Value;
        Assert.Equal("uri", destinationSchema.GetProperty("properties").GetProperty("base_uri").GetProperty("format").GetString());
        Assert.Equal("base_uri", destinationSchema.GetProperty("required")[0].GetString());

        await ExecuteAsync("""
            UPDATE integrations
            SET name = 'Drifted',
                description = 'Drifted description',
                status = 'disabled',
                manifest = jsonb_set(
                    jsonb_set(manifest, '{presentation,name}', '"Drifted"'),
                    '{presentation,description}',
                    '"Drifted description"')
            WHERE key = 'http' AND contract_version = 1
            """);

        IReadOnlyList<Integration> second = await mediator.Send(new BootstrapBuiltinsCommand());
        Integration reconciled = Assert.Single(second);
        Assert.Equal("HTTP", reconciled.Name);
        Assert.Equal(OperationalStatus.Active, reconciled.Status);

        Assert.Equal(1, await CountAsync("integrations", "key = 'http'"));
    }

    [Fact]
    public async Task BootstrapBuiltins_RejectsUnexpectedWellKnownIdentity()
    {
        await ExecuteAsync("DELETE FROM connections");
        await ExecuteAsync("DELETE FROM integrations");
        Guid unexpectedId = Guid.NewGuid();
        string manifest = TestIntegrationManifest.Create(
            "http", "HTTP", "both", description: "Generic HTTP source or destination.");
        await ExecuteAsync($$"""
            INSERT INTO integrations (
                id, key, contract_version, manifest_schema_version, name, direction,
                supported_auth_schemes, status, description, manifest)
            VALUES (
                '{{unexpectedId}}', 'http', 1, 1, 'HTTP', 'both', '[]'::jsonb, 'active',
                'Generic HTTP source or destination.', '{{manifest}}'::jsonb)
            """);

        var exception = await Assert.ThrowsAsync<IntegrationVersionConflictException>(
            () => mediator.Send(new BootstrapBuiltinsCommand()));
        Assert.Contains("unexpected id", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BootstrapAdminKey_CreatesOnce_ThenNoOps()
    {
        await DeleteGlobalAdminKeysAsync();

        BootstrapAdminKeyResult first = await mediator.Send(new BootstrapAdminKeyCommand("global_admin_key", "test-secret"));
        Assert.True(first.Created);
        Assert.Null(first.GeneratedSecret);

        BootstrapAdminKeyResult second = await mediator.Send(new BootstrapAdminKeyCommand("global_admin_key", "test-secret"));
        Assert.False(second.Created);

        Assert.Equal(1, await CountAsync("admin_keys", "revoked_at IS NULL"));
    }

    [Fact]
    public async Task BootstrapAdminKey_GeneratesSecret_WhenNoneSupplied()
    {
        await DeleteGlobalAdminKeysAsync();

        BootstrapAdminKeyResult result = await mediator.Send(new BootstrapAdminKeyCommand("global_admin_key", null));
        Assert.True(result.Created);
        Assert.False(string.IsNullOrWhiteSpace(result.GeneratedSecret));
    }

    [Fact]
    public async Task BootstrapAdminKey_GeneratesSecret_WhenSecretIsEmpty()
    {
        // An unset env var reaches the command as "" (not null); storing SHA256("")
        // would mint a key no auth header can present.
        await DeleteGlobalAdminKeysAsync();

        BootstrapAdminKeyResult result = await mediator.Send(new BootstrapAdminKeyCommand("global_admin_key", ""));
        Assert.True(result.Created);
        Assert.False(string.IsNullOrWhiteSpace(result.GeneratedSecret));
    }

    [Fact]
    public async Task RotateAdminKey_MintsNewPublicKey_AndRevokesPrior_NoUniqueViolation()
    {
        await DeleteGlobalAdminKeysAsync();
        BootstrapAdminKeyResult first = await mediator.Send(new BootstrapAdminKeyCommand("global_admin_key", "first-secret"));
        Assert.True(first.Created);

        RotateAdminKeyResult rotate1 = await mediator.Send(new RotateAdminKeyCommand("rotated-secret-1"));
        Assert.NotEqual("global_admin_key", rotate1.PublicKey);

        RotateAdminKeyResult rotate2 = await mediator.Send(new RotateAdminKeyCommand("rotated-secret-2"));
        Assert.NotEqual(rotate1.PublicKey, rotate2.PublicKey);

        Assert.Equal(1, await CountAsync("admin_keys", "revoked_at IS NULL"));
        Assert.Equal(3, await CountAsync("admin_keys")); // original + 2 rotations retained
        Assert.Equal(Hash("rotated-secret-2"), await ScalarAsync<string>(
            "SELECT secret_hash FROM admin_keys WHERE revoked_at IS NULL"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task RotateAdminKey_RejectsMissingReplacementSecret(string secret)
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            mediator.Send(new RotateAdminKeyCommand(secret)));

        Assert.Equal(1, await CountAsync("admin_keys", "revoked_at IS NULL"));
    }

    [Fact]
    public async Task RotateAdminKey_WithoutLiveKey_RequiresBootstrap()
    {
        await DeleteGlobalAdminKeysAsync();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            mediator.Send(new RotateAdminKeyCommand("replacement-secret")));

        Assert.Contains("Run bootstrap before rotation", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, await CountAsync("admin_keys"));
    }

    [Fact]
    public async Task BootstrapBuiltinsAndAdminKey_CreatesBuiltinsAndDeterministicKey()
    {
        await DeleteGlobalAdminKeysAsync();

        await mediator.Send(new BootstrapBuiltinsCommand());
        BootstrapAdminKeyResult keyResult = await mediator.Send(
            new BootstrapAdminKeyCommand("global_admin_key", "admin_bootstrap_secret"));
        Assert.True(keyResult.Created);

        Assert.Equal(1, await CountAsync("integrations", "key = 'http'"));

        string? secretHash = await ScalarAsync<string>(
            "SELECT secret_hash FROM admin_keys WHERE revoked_at IS NULL");
        Assert.Equal(
            "sha256:5af35a0149f5a07231b181c3b4d5d3a76a4c765258533a123b34dfb843599328",
            secretHash);
    }

    private async Task DeleteGlobalAdminKeysAsync() =>
        await ExecuteAsync("DELETE FROM admin_keys");

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, connection);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<T?> ScalarAsync<T>(string sql)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, connection);
        object? result = await cmd.ExecuteScalarAsync();
        return result is null ? default : (T)result;
    }

    private async Task<long> CountAsync(string table, string where = "TRUE") =>
        await ScalarAsync<long>($"SELECT COUNT(*) FROM {table} WHERE {where}");

    private static string Hash(string secret) =>
        "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret))).ToLowerInvariant();
}
