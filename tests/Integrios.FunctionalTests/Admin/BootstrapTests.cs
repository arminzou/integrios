using System.Security.Cryptography;
using System.Text;
using Dapper;
using Integrios.Application;
using Integrios.Application.Bootstrap;
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
    public async Task BootstrapOperatorKey_LeavesConnectorsEmptyAndCreatesDeterministicKey()
    {
        await ExecuteAsync("DELETE FROM connections");
        await ExecuteAsync("DELETE FROM connectors");
        await DeleteGlobalOperatorKeysAsync();

        BootstrapOperatorKeyResult keyResult = await mediator.Send(
            new BootstrapOperatorKeyCommand("global_operator_key", "operator_bootstrap_secret"));
        keyResult.Created.ShouldBeTrue();

        (await CountAsync("connectors")).ShouldBe(0);

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
