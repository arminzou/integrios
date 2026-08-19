using Integrios.Application.AdminKeys;
using Integrios.Application.Connections;
using Integrios.Domain.Tenants;
using Integrios.Infrastructure.AdminKeys;
using Integrios.Infrastructure.Connections;
using Integrios.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Integrios.Application.FunctionalTests.Infrastructure;

public sealed class ConnectionUsageTests(PostgresApiFixture fixture)
    : IClassFixture<PostgresApiFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetDataAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task AdminKeyLookup_FindsActiveGlobalKey()
    {
        await using (var connection = new NpgsqlConnection(fixture.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                """
                INSERT INTO admin_keys (public_key, secret_hash, name)
                VALUES ('global_admin_key', 'sha256:test', 'Test key')
                """,
                connection);
            await command.ExecuteNonQueryAsync();
        }

        await using IntegriosDbContext context = CreateContext();
        IAdminKeyLookup repository = new AdminKeyRepository(context);

        AdminKey? key = await repository.FindActiveByPublicKeyAsync(
            "global_admin_key",
            CancellationToken.None);

        Assert.NotNull(key);
        Assert.Equal("sha256:test", key.SecretHash);
    }

    [Fact]
    public async Task Usage_CountsActiveAssociations_AndIgnoresRetiredOnes()
    {
        Guid topicId = await fixture.SeedTopicAsync(fixture.TenantAId, "usage-topic");
        Guid connectionId = await fixture.SeedSourceConnectionAsync(fixture.TenantAId, "usage-source");
        await fixture.AssociateSourceAsync(fixture.TenantAId, topicId, connectionId);

        await using IntegriosDbContext context = CreateContext();
        var repository = new ConnectionRepository(context);

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

    private IntegriosDbContext CreateContext() => new(
        new DbContextOptionsBuilder<IntegriosDbContext>()
            .UseNpgsql(fixture.ConnectionString)
            .Options);
}
