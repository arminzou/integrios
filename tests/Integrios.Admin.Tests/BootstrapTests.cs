using Integrios.Application;
using Integrios.Application.Bootstrap;
using Integrios.Domain.Common;
using Integrios.Domain.Integrations;
using Integrios.Infrastructure;
using MediatR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Integrios.Admin.Tests;

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
        services.AddIntegriosApplication();
        services.AddIntegriosInfrastructure(configuration);
        provider = services.BuildServiceProvider();
        mediator = provider.GetRequiredService<IMediator>();
    }

    public Task DisposeAsync()
    {
        provider.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task BootstrapBuiltins_IsIdempotent_AndReconcilesDrift()
    {
        IReadOnlyList<Integration> first = await mediator.Send(new BootstrapBuiltinsCommand());
        Integration webhook = Assert.Single(first, i => i.Key == "webhook");
        Assert.Equal(BuiltinCatalog.WebhookId, webhook.Id);
        Assert.Equal(IntegrationDirection.Both, webhook.Direction);
        Assert.Empty(webhook.SupportedAuthSchemes);

        await ExecuteAsync("UPDATE integrations SET name = 'Drifted', status = 'disabled' WHERE key = 'webhook'");

        IReadOnlyList<Integration> second = await mediator.Send(new BootstrapBuiltinsCommand());
        Integration reconciled = Assert.Single(second);
        Assert.Equal("Webhook", reconciled.Name);
        Assert.Equal(OperationalStatus.Active, reconciled.Status);

        Assert.Equal(1, await CountAsync("integrations", "key = 'webhook'"));
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

        Assert.Equal(1, await CountAsync("admin_keys", "tenant_id IS NULL AND revoked_at IS NULL"));
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
    public async Task RotateGlobalAdminKey_MintsNewPublicKey_AndRevokesPrior_NoUniqueViolation()
    {
        await DeleteGlobalAdminKeysAsync();
        BootstrapAdminKeyResult first = await mediator.Send(new BootstrapAdminKeyCommand("global_admin_key", "first-secret"));
        Assert.True(first.Created);

        RotateAdminKeyResult rotate1 = await mediator.Send(new RotateAdminKeyCommand("rotated-secret-1"));
        Assert.NotEqual("global_admin_key", rotate1.PublicKey);

        RotateAdminKeyResult rotate2 = await mediator.Send(new RotateAdminKeyCommand("rotated-secret-2"));
        Assert.NotEqual(rotate1.PublicKey, rotate2.PublicKey);

        Assert.Equal(1, await CountAsync("admin_keys", "tenant_id IS NULL AND revoked_at IS NULL"));
        Assert.Equal(3, await CountAsync("admin_keys", "tenant_id IS NULL")); // original + 2 rotations retained
    }

    [Fact]
    public async Task BootstrapDev_CreatesBuiltinsAndDeterministicKey()
    {
        await DeleteGlobalAdminKeysAsync();

        await mediator.Send(new BootstrapBuiltinsCommand());
        BootstrapAdminKeyResult keyResult = await mediator.Send(
            new BootstrapAdminKeyCommand("global_admin_key", "admin_bootstrap_secret"));
        Assert.True(keyResult.Created);

        Assert.Equal(1, await CountAsync("integrations", "key = 'webhook'"));

        string? secretHash = await ScalarAsync<string>(
            "SELECT secret_hash FROM admin_keys WHERE tenant_id IS NULL AND revoked_at IS NULL");
        Assert.Equal(
            "sha256:5af35a0149f5a07231b181c3b4d5d3a76a4c765258533a123b34dfb843599328",
            secretHash);
    }

    private async Task DeleteGlobalAdminKeysAsync() =>
        await ExecuteAsync("DELETE FROM admin_keys WHERE tenant_id IS NULL");

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

    private async Task<long> CountAsync(string table, string where) =>
        await ScalarAsync<long>($"SELECT COUNT(*) FROM {table} WHERE {where}");
}
