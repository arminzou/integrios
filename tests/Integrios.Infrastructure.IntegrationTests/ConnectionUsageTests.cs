using Integrios.Application.Connections;
using Integrios.Infrastructure.Connections;
using Integrios.Infrastructure.Data;
using Npgsql;

namespace Integrios.Infrastructure.IntegrationTests;

public sealed class ConnectionUsageTests(PostgresApiFixture fixture)
    : IClassFixture<PostgresApiFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetDataAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Usage_CountsActiveAssociations_AndIgnoresRetiredOnes()
    {
        Guid topicId = await fixture.SeedTopicAsync(fixture.TenantAId, "usage-topic");
        Guid connectionId = await fixture.SeedSourceConnectionAsync(fixture.TenantAId, "usage-source");
        await fixture.AssociateSourceAsync(fixture.TenantAId, topicId, connectionId);

        await using NpgsqlDataSource dataSource = new NpgsqlDataSourceBuilder(fixture.ConnectionString).Build();
        var repository = new PostgresConnectionRepository(new NpgsqlConnectionFactory(dataSource));

        ConnectionUsage whileAssociated = await repository.GetUsageAsync(
            fixture.TenantAId, connectionId, CancellationToken.None);
        Assert.True(whileAssociated.Source);

        await fixture.RetireSourceAsync(fixture.TenantAId, topicId, connectionId);

        // The tombstone stays in the table for historical Event foreign keys, but a Connection no
        // longer associated with any Topic is not in source use and must not stay source-constrained
        // when it is next updated.
        ConnectionUsage afterRetirement = await repository.GetUsageAsync(
            fixture.TenantAId, connectionId, CancellationToken.None);
        Assert.False(afterRetirement.Source);
        Assert.False(afterRetirement.Destination);
    }
}
