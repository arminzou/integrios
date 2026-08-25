using Dapper;
using Integrios.Domain.Enums;
using Integrios.Infrastructure.Data;
using Integrios.Infrastructure.Secrets;

namespace Integrios.FunctionalTests.Infrastructure;

public sealed class SecretValidationReaderTests : IClassFixture<PostgresApiFixture>, IAsyncLifetime
{
    private readonly PostgresApiFixture fixture;

    public SecretValidationReaderTests(PostgresApiFixture fixture)
    {
        this.fixture = fixture;
    }

    public Task InitializeAsync() => fixture.ResetDataAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Reader_OwnsActiveEnumeration_ButFindsDisabledSelections()
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
        var reader = new SecretValidationReader(context);

        var selectedTenant = await reader.FindTenantBySlugAsync("test-tenant-b", CancellationToken.None);
        selectedTenant.ShouldNotBeNull();
        selectedTenant.Status.ShouldBe(OperationalStatus.Disabled);
        (await reader.ListActiveTenantsAsync(CancellationToken.None)).ShouldNotContain(tenant => tenant.Id == fixture.TenantBId);

        var selectedConnection = await reader.FindConnectionAsync(fixture.TenantAId, disabledConnectionId, CancellationToken.None);
        selectedConnection.ShouldNotBeNull();
        selectedConnection.Status.ShouldBe(OperationalStatus.Disabled);

        var activeConnections = await reader.ListActiveConnectionsAsync(fixture.TenantAId, CancellationToken.None);
        activeConnections.ShouldContain(connection => connection.Id == activeConnectionId);
        activeConnections.ShouldNotContain(connection => connection.Id == disabledConnectionId);
    }
}
