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
        Guid activeDestinationId = await fixture.SeedDestinationAsync(
            fixture.TenantAId,
            "active-source");
        Guid disabledDestinationId = await fixture.SeedDestinationAsync(
            fixture.TenantAId,
            "disabled-destination");

        await using (var connection = fixture.CreateConnection())
        {
            await connection.OpenAsync();
            await connection.ExecuteAsync(
                """
                UPDATE tenants SET status = 'disabled' WHERE id = @DisabledTenantId;
                UPDATE destinations SET status = 'disabled' WHERE id = @DisabledDestinationId;
                """,
                new { DisabledTenantId = fixture.TenantBId, DisabledDestinationId = disabledDestinationId });
        }

        await using var context = new IntegriosDbContext(fixture.CreateOptions());
        var reader = new SecretValidationReader(context);

        var selectedTenant = await reader.FindTenantBySlugAsync("test-tenant-b", CancellationToken.None);
        selectedTenant.ShouldNotBeNull();
        selectedTenant.Status.ShouldBe(OperationalStatus.Disabled);
        (await reader.ListActiveTenantsAsync(CancellationToken.None)).ShouldNotContain(tenant => tenant.Id == fixture.TenantBId);

        var selectedDestination = await reader.FindDestinationAsync(fixture.TenantAId, disabledDestinationId, CancellationToken.None);
        selectedDestination.ShouldNotBeNull();
        selectedDestination.Status.ShouldBe(OperationalStatus.Disabled);

        var activeDestinations = await reader.ListActiveDestinationsAsync(fixture.TenantAId, CancellationToken.None);
        activeDestinations.ShouldContain(destination => destination.Id == activeDestinationId);
        activeDestinations.ShouldNotContain(destination => destination.Id == disabledDestinationId);
    }
}
