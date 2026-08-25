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

namespace Integrios.FunctionalTests.Admin;

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
        first.Count.ShouldBe(3);
        Connector http = first.Where(i => i.Key == "http").ShouldHaveSingleItem();
        http.Id.ShouldBe(BuiltinCatalog.HttpId);
        http.Direction.ShouldBe(ConnectorDirection.Both);
        http.Manifest.DestinationAuthentication.Schemes.Select(s => s.Scheme).Order(StringComparer.Ordinal)
            .ShouldBe(["api_key_header", "bearer_token"]);
        http.Manifest.SourceContracts.ShouldHaveSingleItem().Key.ShouldBe("event_json");
        JsonElement destinationSchema = http.Manifest.DestinationConfigurationSchema!.Value;
        destinationSchema.GetProperty("properties").GetProperty("base_uri").GetProperty("format").GetString().ShouldBe("uri");
        destinationSchema.GetProperty("required")[0].GetString().ShouldBe("base_uri");
        Connector github = first.Where(i => i.Key == "github").ShouldHaveSingleItem();
        github.Id.ShouldBe(BuiltinCatalog.GitHubId);
        github.Manifest.SourceContracts.ShouldHaveSingleItem().Key.ShouldBe("github_webhook");
        Connector dataverse = first.Where(i => i.Key == "dataverse").ShouldHaveSingleItem();
        dataverse.Id.ShouldBe(BuiltinCatalog.DataverseId);
        dataverse.Manifest.SourceContracts.ShouldHaveSingleItem().Key.ShouldBe("remote_execution_context_json");

        await ExecuteAsync($$$"""
            UPDATE connectors
            SET name = 'Drifted',
                description = 'Drifted description',
                status = 'disabled',
                manifest = {{{fixture.PresentationDriftExpression}}}
            WHERE {{{fixture.KeyColumn}}} = 'http' AND contract_version = 1
            """);

        IReadOnlyList<Connector> second = await mediator.Send(new BootstrapBuiltinsCommand());
        second.Count.ShouldBe(3);
        Connector reconciled = second.Where(item => item.Key == "http").ShouldHaveSingleItem();
        reconciled.Name.ShouldBe("HTTP");
        reconciled.Status.ShouldBe(OperationalStatus.Active);

        (await CountAsync("connectors", $"{fixture.KeyColumn} = 'http'")).ShouldBe(1);
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
                status, description, manifest)
            VALUES (
                @Id, 'http', 1, 1, 'HTTP', 'both', 'active',
                'Generic HTTP source or destination.', {{{fixture.Json("@Manifest")}}})
            """, new { Id = unexpectedId, Manifest = manifest });

        var exception = await Should.ThrowAsync<ConnectorVersionConflictException>(
            () => mediator.Send(new BootstrapBuiltinsCommand()));
        exception.Message.ShouldContain("unexpected id", Case.Insensitive);
    }

    [Fact]
    public async Task BootstrapOperatorKey_CreatesOnce_ThenNoOps()
    {
        await DeleteGlobalOperatorKeysAsync();

        BootstrapOperatorKeyResult first = await mediator.Send(new BootstrapOperatorKeyCommand("global_operator_key", "test-secret"));
        first.Created.ShouldBeTrue();
        first.GeneratedSecret.ShouldBeNull();

        BootstrapOperatorKeyResult second = await mediator.Send(new BootstrapOperatorKeyCommand("global_operator_key", "test-secret"));
        second.Created.ShouldBeFalse();

        (await CountAsync("operator_keys", "revoked_at IS NULL")).ShouldBe(1);
    }

    [Fact]
    public async Task BootstrapOperatorKey_GeneratesSecret_WhenNoneSupplied()
    {
        await DeleteGlobalOperatorKeysAsync();

        BootstrapOperatorKeyResult result = await mediator.Send(new BootstrapOperatorKeyCommand("global_operator_key", null));
        result.Created.ShouldBeTrue();
        string.IsNullOrWhiteSpace(result.GeneratedSecret).ShouldBeFalse();
    }

    [Fact]
    public async Task BootstrapOperatorKey_GeneratesSecret_WhenSecretIsEmpty()
    {
        // An unset env var reaches the command as "" (not null); storing SHA256("")
        // would mint a key no auth header can present.
        await DeleteGlobalOperatorKeysAsync();

        BootstrapOperatorKeyResult result = await mediator.Send(new BootstrapOperatorKeyCommand("global_operator_key", ""));
        result.Created.ShouldBeTrue();
        string.IsNullOrWhiteSpace(result.GeneratedSecret).ShouldBeFalse();
    }

    [Fact]
    public async Task RotateOperatorKey_MintsNewPublicKey_AndRevokesPrior_NoUniqueViolation()
    {
        await DeleteGlobalOperatorKeysAsync();
        BootstrapOperatorKeyResult first = await mediator.Send(new BootstrapOperatorKeyCommand("global_operator_key", "first-secret"));
        first.Created.ShouldBeTrue();

        RotateOperatorKeyResult rotate1 = await mediator.Send(new RotateOperatorKeyCommand("rotated-secret-1"));
        rotate1.PublicKey.ShouldNotBe("global_operator_key");

        RotateOperatorKeyResult rotate2 = await mediator.Send(new RotateOperatorKeyCommand("rotated-secret-2"));
        rotate2.PublicKey.ShouldNotBe(rotate1.PublicKey);

        (await CountAsync("operator_keys", "revoked_at IS NULL")).ShouldBe(1);
        (await CountAsync("operator_keys")).ShouldBe(3); // original + 2 rotations retained
        (await ScalarAsync<string>(
            "SELECT secret_hash FROM operator_keys WHERE revoked_at IS NULL")).ShouldBe(Hash("rotated-secret-2"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task RotateOperatorKey_RejectsMissingReplacementSecret(string secret)
    {
        await Should.ThrowAsync<ArgumentException>(() =>
            mediator.Send(new RotateOperatorKeyCommand(secret)));

        (await CountAsync("operator_keys", "revoked_at IS NULL")).ShouldBe(1);
    }

    [Fact]
    public async Task RotateOperatorKey_WithoutLiveKey_RequiresBootstrap()
    {
        await DeleteGlobalOperatorKeysAsync();

        var exception = await Should.ThrowAsync<InvalidOperationException>(() =>
            mediator.Send(new RotateOperatorKeyCommand("replacement-secret")));

        exception.Message.ShouldContain("Run bootstrap before rotation", Case.Sensitive);
        (await CountAsync("operator_keys")).ShouldBe(0);
    }

    [Fact]
    public async Task BootstrapBuiltinsAndOperatorKey_CreatesBuiltinsAndDeterministicKey()
    {
        await DeleteGlobalOperatorKeysAsync();

        await mediator.Send(new BootstrapBuiltinsCommand());
        BootstrapOperatorKeyResult keyResult = await mediator.Send(
            new BootstrapOperatorKeyCommand("global_operator_key", "operator_bootstrap_secret"));
        keyResult.Created.ShouldBeTrue();

        (await CountAsync("connectors", $"{fixture.KeyColumn} = 'http'")).ShouldBe(1);

        string? secretHash = await ScalarAsync<string>(
            "SELECT secret_hash FROM operator_keys WHERE revoked_at IS NULL");
        secretHash.ShouldBe(
            "sha256:e98f79daedd50eea3a83ba72c3cd33802bcb5432a6e6273d1fe0bf573dfe8420");
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
