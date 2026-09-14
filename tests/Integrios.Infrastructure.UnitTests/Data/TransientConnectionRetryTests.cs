using System.Data.Common;
using Integrios.Infrastructure.Data;

namespace Integrios.Infrastructure.UnitTests;

public sealed class TransientConnectionRetryTests
{
    [Fact]
    public async Task TransientOpenFailure_IsRetriedUntilTheConnectionOpens()
    {
        int attempts = 0;

        DbConnection connection = await TransientConnectionRetry.OpenAsync(
            _ =>
            {
                attempts++;
                if (attempts < 3)
                    throw new FakeDbException(isTransient: true);
                return ValueTask.FromResult<DbConnection>(new FakeDbConnection());
            },
            CancellationToken.None);

        attempts.ShouldBe(3);
        connection.ShouldBeOfType<FakeDbConnection>();
    }

    [Fact]
    public async Task NonTransientOpenFailure_IsNotRetried()
    {
        int attempts = 0;

        await Should.ThrowAsync<FakeDbException>(() => TransientConnectionRetry.OpenAsync(
            _ =>
            {
                attempts++;
                throw new FakeDbException(isTransient: false);
            },
            CancellationToken.None).AsTask());

        attempts.ShouldBe(1);
    }

    [Fact]
    public async Task TransientOpenFailure_StopsRetryingAndSurfacesTheFaultWhenItPersists()
    {
        int attempts = 0;

        await Should.ThrowAsync<FakeDbException>(() => TransientConnectionRetry.OpenAsync(
            _ =>
            {
                attempts++;
                throw new FakeDbException(isTransient: true);
            },
            CancellationToken.None).AsTask());

        attempts.ShouldBe(4);
    }

    private sealed class FakeDbException(bool isTransient) : DbException("Planted open failure.")
    {
        public override bool IsTransient { get; } = isTransient;
    }

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
