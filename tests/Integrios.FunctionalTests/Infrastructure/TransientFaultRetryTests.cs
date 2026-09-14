using System.Data.Common;
using Integrios.Infrastructure;
using Integrios.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace Integrios.FunctionalTests.Infrastructure;

// A transient fault is planted in front of a real command so the retry has to engage for the
// operation to complete at all. Asserting the option is set would pass even if the execution
// strategy never ran -- which is how a user-initiated transaction silently loses its retry.
public sealed class TransientFaultRetryTests(DatabaseProviderFixture fixture)
    : IClassFixture<DatabaseProviderFixture>
{
    [Fact]
    public async Task PlantedTransientFault_DuringQuery_IsRetriedAndTheQuerySucceeds()
    {
        PlantedTransientFaultInterceptor interceptor = new(faultsToPlant: 1);
        await using IntegriosDbContext context = CreateContext(interceptor);

        int connectors = await context.Connectors.CountAsync();

        connectors.ShouldBeGreaterThanOrEqualTo(0);
        interceptor.Attempts.ShouldBe(2);
    }

    [Fact]
    public async Task PlantedTransientFault_DuringStartupMigration_IsRetriedAndMigrationCompletes()
    {
        PlantedTransientFaultInterceptor interceptor = new(faultsToPlant: 1);
        await using ServiceProvider provider = new ServiceCollection()
            .AddDbContext<IntegriosDbContext>(options => ConfigureProvider(options)
                .AddInterceptors(interceptor))
            .BuildServiceProvider();

        await provider.MigrateDatabaseAsync();

        interceptor.Attempts.ShouldBeGreaterThan(1);
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        IntegriosDbContext context = scope.ServiceProvider.GetRequiredService<IntegriosDbContext>();
        (await context.Database.GetPendingMigrationsAsync()).ShouldBeEmpty();
    }

    // Built the way the composition root builds it, so the retry under test is the registered one.
    private IntegriosDbContext CreateContext(IInterceptor interceptor) => new(
        (DbContextOptions<IntegriosDbContext>)ConfigureProvider(
            new DbContextOptionsBuilder<IntegriosDbContext>()).AddInterceptors(interceptor).Options);

    private DbContextOptionsBuilder ConfigureProvider(DbContextOptionsBuilder builder) =>
        builder.UseIntegriosProvider(
            fixture.Database.Provider == "sqlserver"
                ? DatabaseProvider.SqlServer
                : DatabaseProvider.Postgres,
            fixture.Database.ConnectionString);

    private sealed class PlantedTransientFaultInterceptor(int faultsToPlant) : DbCommandInterceptor
    {
        public int Attempts { get; private set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Fault();
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<object> result,
            CancellationToken cancellationToken = default)
        {
            Fault();
            return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Fault();
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        private void Fault()
        {
            Attempts++;
            // Both providers' transient-fault detectors treat a timeout as retryable, which keeps
            // the planted fault provider-neutral.
            if (Attempts <= faultsToPlant)
                throw new TimeoutException("Planted transient fault.");
        }
    }
}
