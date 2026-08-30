using System.Data.Common;
using Integrios.Infrastructure.Data;
using Integrios.Infrastructure.Hosting;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using NSubstitute;

namespace Integrios.Infrastructure.UnitTests;

public sealed class DatabaseReadinessHealthCheckTests
{
    [Fact]
    public async Task CheckHealthAsync_OpenConnection_IsHealthy()
    {
        var connection = Substitute.For<DbConnection>();
        var command = Substitute.For<DbCommand>();
        connection.CreateCommand().Returns(command);
        command.ExecuteScalarAsync(Arg.Any<CancellationToken>()).Returns(1);
        var factory = new StubConnectionFactory(connection);
        var check = new DatabaseReadinessHealthCheck(factory);

        HealthCheckResult result = await check.CheckHealthAsync(new HealthCheckContext());

        result.Status.ShouldBe(HealthStatus.Healthy);
        await command.Received(1).ExecuteScalarAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CheckHealthAsync_ConnectionFailure_IsUnhealthy()
    {
        var factory = new StubConnectionFactory(new InvalidOperationException("offline"));
        var check = new DatabaseReadinessHealthCheck(factory);

        HealthCheckResult result = await check.CheckHealthAsync(new HealthCheckContext());

        result.Status.ShouldBe(HealthStatus.Unhealthy);
        result.Exception.ShouldNotBeNull();
    }

    private sealed class StubConnectionFactory : IDbConnectionFactory
    {
        private readonly DbConnection? connection;
        private readonly Exception? exception;

        public StubConnectionFactory(DbConnection connection) => this.connection = connection;

        public StubConnectionFactory(Exception exception) => this.exception = exception;

        public DatabaseProvider Provider => DatabaseProvider.Postgres;

        public ValueTask<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken) =>
            exception is null
                ? ValueTask.FromResult(connection!)
                : ValueTask.FromException<DbConnection>(exception);
    }
}
