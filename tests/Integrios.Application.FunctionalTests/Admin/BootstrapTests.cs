using Integrios.Tests.Shared;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Integrios.Application;
using Integrios.Application.Bootstrap;
using Integrios.Application.Authoring.Connectors;
using Integrios.Domain.Entities;
using Integrios.Domain.Enums;
using Integrios.Domain.ValueObjects;
using Integrios.Infrastructure;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

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

        var services = new ServiceCollection();
        services.AddAdminApplicationServices();
        services.AddAdminInfrastructureServices(fixture.Configuration);
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
        IReadOnlyList<Connector> first = await mediator.Send(new BootstrapBuiltinsCommand());
        Assert.Equal(2, first.Count);
        Connector http = Assert.Single(first, i => i.Key == "http");
        Assert.Equal(BuiltinCatalog.HttpId, http.Id);
        Assert.Equal(ConnectorDirection.Both, http.Direction);
        Assert.Equal(["api_key_header", "bearer_token"], http.SupportedAuthSchemes.Order(StringComparer.Ordinal));
        Assert.Equal("event_json", Assert.Single(http.Manifest.SourceContracts).Key);
        JsonElement destinationSchema = http.Manifest.DestinationConfigurationSchema!.Value;
        Assert.Equal("uri", destinationSchema.GetProperty("properties").GetProperty("base_uri").GetProperty("format").GetString());
        Assert.Equal("base_uri", destinationSchema.GetProperty("required")[0].GetString());
        Connector github = Assert.Single(first, i => i.Key == "github");
        Assert.Equal(BuiltinCatalog.GitHubId, github.Id);
        Assert.Equal("github_webhook", Assert.Single(github.Manifest.SourceContracts).Key);

        await ExecuteAsync($$$"""
            UPDATE connectors
            SET name = 'Drifted',
                description = 'Drifted description',
                status = 'disabled',
                manifest = {{{fixture.PresentationDriftExpression}}}
            WHERE {{{fixture.KeyColumn}}} = 'http' AND contract_version = 1
            """);

        IReadOnlyList<Connector> second = await mediator.Send(new BootstrapBuiltinsCommand());
        Assert.Equal(2, second.Count);
        Connector reconciled = Assert.Single(second, item => item.Key == "http");
        Assert.Equal("HTTP", reconciled.Name);
        Assert.Equal(OperationalStatus.Active, reconciled.Status);

        Assert.Equal(1, await CountAsync("connectors", $"{fixture.KeyColumn} = 'http'"));
    }

    [Fact]
    public async Task BootstrapBuiltins_RejectsUnexpectedWellKnownIdentity()
    {
        await ExecuteAsync("DELETE FROM connections");
        await ExecuteAsync("DELETE FROM connectors");
        Guid unexpectedId = Guid.NewGuid();
        string manifest = TestConnectorManifest.Create(
            "http", "HTTP", "both", description: "Generic HTTP source or destination.");
        await ExecuteAsync($$$"""
            INSERT INTO connectors (
                id, {{{fixture.KeyColumn}}}, contract_version, manifest_schema_version, name, direction,
                supported_auth_schemes, status, description, manifest)
            VALUES (
                @Id, 'http', 1, 1, 'HTTP', 'both', {{{fixture.Json("@Schemes")}}}, 'active',
                'Generic HTTP source or destination.', {{{fixture.Json("@Manifest")}}})
            """, new { Id = unexpectedId, Schemes = "[]", Manifest = manifest });

        var exception = await Assert.ThrowsAsync<ConnectorVersionConflictException>(
            () => mediator.Send(new BootstrapBuiltinsCommand()));
        Assert.Contains("unexpected id", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BootstrapOperatorKey_CreatesOnce_ThenNoOps()
    {
        await DeleteGlobalOperatorKeysAsync();

        BootstrapOperatorKeyResult first = await mediator.Send(new BootstrapOperatorKeyCommand("global_operator_key", "test-secret"));
        Assert.True(first.Created);
        Assert.Null(first.GeneratedSecret);

        BootstrapOperatorKeyResult second = await mediator.Send(new BootstrapOperatorKeyCommand("global_operator_key", "test-secret"));
        Assert.False(second.Created);

        Assert.Equal(1, await CountAsync("operator_keys", "revoked_at IS NULL"));
    }

    [Fact]
    public async Task BootstrapOperatorKey_GeneratesSecret_WhenNoneSupplied()
    {
        await DeleteGlobalOperatorKeysAsync();

        BootstrapOperatorKeyResult result = await mediator.Send(new BootstrapOperatorKeyCommand("global_operator_key", null));
        Assert.True(result.Created);
        Assert.False(string.IsNullOrWhiteSpace(result.GeneratedSecret));
    }

    [Fact]
    public async Task BootstrapOperatorKey_GeneratesSecret_WhenSecretIsEmpty()
    {
        // An unset env var reaches the command as "" (not null); storing SHA256("")
        // would mint a key no auth header can present.
        await DeleteGlobalOperatorKeysAsync();

        BootstrapOperatorKeyResult result = await mediator.Send(new BootstrapOperatorKeyCommand("global_operator_key", ""));
        Assert.True(result.Created);
        Assert.False(string.IsNullOrWhiteSpace(result.GeneratedSecret));
    }

    [Fact]
    public async Task RotateOperatorKey_MintsNewPublicKey_AndRevokesPrior_NoUniqueViolation()
    {
        await DeleteGlobalOperatorKeysAsync();
        BootstrapOperatorKeyResult first = await mediator.Send(new BootstrapOperatorKeyCommand("global_operator_key", "first-secret"));
        Assert.True(first.Created);

        RotateOperatorKeyResult rotate1 = await mediator.Send(new RotateOperatorKeyCommand("rotated-secret-1"));
        Assert.NotEqual("global_operator_key", rotate1.PublicKey);

        RotateOperatorKeyResult rotate2 = await mediator.Send(new RotateOperatorKeyCommand("rotated-secret-2"));
        Assert.NotEqual(rotate1.PublicKey, rotate2.PublicKey);

        Assert.Equal(1, await CountAsync("operator_keys", "revoked_at IS NULL"));
        Assert.Equal(3, await CountAsync("operator_keys")); // original + 2 rotations retained
        Assert.Equal(Hash("rotated-secret-2"), await ScalarAsync<string>(
            "SELECT secret_hash FROM operator_keys WHERE revoked_at IS NULL"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task RotateOperatorKey_RejectsMissingReplacementSecret(string secret)
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            mediator.Send(new RotateOperatorKeyCommand(secret)));

        Assert.Equal(1, await CountAsync("operator_keys", "revoked_at IS NULL"));
    }

    [Fact]
    public async Task RotateOperatorKey_WithoutLiveKey_RequiresBootstrap()
    {
        await DeleteGlobalOperatorKeysAsync();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            mediator.Send(new RotateOperatorKeyCommand("replacement-secret")));

        Assert.Contains("Run bootstrap before rotation", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, await CountAsync("operator_keys"));
    }

    [Fact]
    public async Task BootstrapBuiltinsAndOperatorKey_CreatesBuiltinsAndDeterministicKey()
    {
        await DeleteGlobalOperatorKeysAsync();

        await mediator.Send(new BootstrapBuiltinsCommand());
        BootstrapOperatorKeyResult keyResult = await mediator.Send(
            new BootstrapOperatorKeyCommand("global_operator_key", "operator_bootstrap_secret"));
        Assert.True(keyResult.Created);

        Assert.Equal(1, await CountAsync("connectors", $"{fixture.KeyColumn} = 'http'"));

        string? secretHash = await ScalarAsync<string>(
            "SELECT secret_hash FROM operator_keys WHERE revoked_at IS NULL");
        Assert.Equal(
            "sha256:e98f79daedd50eea3a83ba72c3cd33802bcb5432a6e6273d1fe0bf573dfe8420",
            secretHash);
    }

    private async Task DeleteGlobalOperatorKeysAsync() =>
        await ExecuteAsync("DELETE FROM operator_keys");

    private async Task ExecuteAsync(string sql, object? parameters = null)
    {
        await using var connection = fixture.CreateConnection();
        await connection.OpenAsync();
        await connection.ExecuteAsync(sql, parameters);
    }

    private async Task<T?> ScalarAsync<T>(string sql)
    {
        await using var connection = fixture.CreateConnection();
        await connection.OpenAsync();
        object? result = await connection.ExecuteScalarAsync(sql);
        if (result is null or DBNull)
            return default;
        return result is T value ? value : (T)Convert.ChangeType(result, typeof(T));
    }

    private async Task<long> CountAsync(string table, string where = "1=1") =>
        await ScalarAsync<long>($"SELECT COUNT(*) FROM {table} WHERE {where}");

    private static string Hash(string secret) =>
        "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret))).ToLowerInvariant();
}
