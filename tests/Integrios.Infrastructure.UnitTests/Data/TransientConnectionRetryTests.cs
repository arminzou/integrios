using System.Data.Common;
using Integrios.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Integrios.Infrastructure.UnitTests;

// The fault is planted in the open itself and classified by the provider's own execution strategy,
// so these assert the registered retry policy rather than a hand-written transient check. An
// earlier hand-written check read DbException.IsTransient, which Microsoft.Data.SqlClient never
// overrides -- it retried Npgsql and did nothing at all on SQL Server. No database is contacted:
// building the strategy needs the registration, not a live server.
public sealed class TransientConnectionRetryTests
{
    [Theory]
    [InlineData("postgres")]
    [InlineData("sqlserver")]
    public async Task TransientOpenFailure_IsRetriedUntilTheConnectionOpens(string provider)
    {
        int attempts = 0;
        using ServiceProvider services = BuildServices(provider);

        DbConnection connection = await TransientConnectionRetry.OpenAsync(
            ContextFactory(services),
            _ =>
            {
                attempts++;
                if (attempts < 3)
                    throw new TimeoutException("Planted transient fault.");
                return Task.FromResult<DbConnection>(new FakeDbConnection());
            },
            CancellationToken.None);

        attempts.ShouldBe(3);
        connection.ShouldBeOfType<FakeDbConnection>();
    }

    [Theory]
    [InlineData("postgres")]
    [InlineData("sqlserver")]
    public async Task NonTransientOpenFailure_IsNotRetried(string provider)
    {
        int attempts = 0;
        using ServiceProvider services = BuildServices(provider);

        await Should.ThrowAsync<InvalidOperationException>(() => TransientConnectionRetry.OpenAsync(
            ContextFactory(services),
            _ =>
            {
                attempts++;
                throw new InvalidOperationException("Planted permanent fault.");
            },
            CancellationToken.None).AsTask());

        attempts.ShouldBe(1);
    }

    private static IDbContextFactory<IntegriosDbContext> ContextFactory(ServiceProvider services) =>
        services.GetRequiredService<IDbContextFactory<IntegriosDbContext>>();

    private static ServiceProvider BuildServices(string provider) => new ServiceCollection()
        .AddAdminInfrastructureServices(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Provider"] = provider,
                ["ConnectionStrings:Postgres"] =
                    "Host=localhost;Database=integrios;Username=integrios;Password=integrios",
                ["ConnectionStrings:SqlServer"] =
                    "Server=localhost;Database=integrios;User Id=sa;Password=Integrios_Test_2026!;TrustServerCertificate=True"
            })
            .Build())
        .BuildServiceProvider();

    private sealed class FakeDbConnection : DbConnection
    {
        public override string ConnectionString { get; set; } = string.Empty;
        public override string Database => string.Empty;
        public override string DataSource => string.Empty;
        public override string ServerVersion => string.Empty;
        public override System.Data.ConnectionState State => System.Data.ConnectionState.Open;
        public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();
        public override void Close() { }
        public override void Open() { }
        protected override DbTransaction BeginDbTransaction(System.Data.IsolationLevel isolationLevel) =>
            throw new NotSupportedException();
        protected override DbCommand CreateDbCommand() => throw new NotSupportedException();
    }
}
