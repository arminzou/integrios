using Dapper;
using Integrios.Domain.Enums;
using Integrios.Infrastructure.Data;
using Integrios.Infrastructure.Secrets;

namespace Integrios.Application.FunctionalTests.Infrastructure;

public sealed class SecretValidationCatalogTests : IClassFixture<PostgresApiFixture>, IAsyncLifetime
{
    private readonly PostgresApiFixture fixture;

    public SecretValidationCatalogTests(PostgresApiFixture fixture)
    {
        this.fixture = fixture;
    }

    public Task InitializeAsync() => fixture.ResetDataAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Catalog_OwnsActiveEnumeration_ButFindsDisabledSelections()
    {
        Guid activeConnectionId = await fixture.SeedSourceConnectionAsync(
            fixture.TenantAId,
            "active-source");
        Guid disabledConnectionId = await fixture.SeedSourceConnectionAsync(
            fixture.TenantAId,
            "disabled-source");

        await using (var connection = fixture.CreateConnection())
        {
            await connection.OpenAsync();
            await connection.ExecuteAsync(
                """
                UPDATE tenants SET status = 'disabled' WHERE id = @DisabledTenantId;
                UPDATE connections SET status = 'disabled' WHERE id = @DisabledConnectionId;
                """,
                new { DisabledTenantId = fixture.TenantBId, DisabledConnectionId = disabledConnectionId });
        }

        await using var context = new IntegriosDbContext(fixture.CreateOptions());
        var catalog = new SecretValidationCatalog(context);

        var selectedTenant = await catalog.FindTenantBySlugAsync("test-tenant-b", CancellationToken.None);
        Assert.NotNull(selectedTenant);
        Assert.Equal(OperationalStatus.Disabled, selectedTenant.Status);
        Assert.DoesNotContain(await catalog.ListActiveTenantsAsync(CancellationToken.None), tenant => tenant.Id == fixture.TenantBId);

        var selectedConnection = await catalog.FindConnectionAsync(fixture.TenantAId, disabledConnectionId, CancellationToken.None);
        Assert.NotNull(selectedConnection);
        Assert.Equal(OperationalStatus.Disabled, selectedConnection.Status);

        var activeConnections = await catalog.ListActiveConnectionsAsync(fixture.TenantAId, CancellationToken.None);
        Assert.Contains(activeConnections, connection => connection.Id == activeConnectionId);
        Assert.DoesNotContain(activeConnections, connection => connection.Id == disabledConnectionId);
    }
}
