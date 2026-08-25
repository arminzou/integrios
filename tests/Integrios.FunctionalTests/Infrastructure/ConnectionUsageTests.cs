using Dapper;
using Integrios.Application.Authoring.OperatorKeys;
using Integrios.Application.Authoring.Connections;
using Integrios.Domain.Entities;
using Integrios.Domain.Enums;
using Integrios.Domain.ValueObjects;
using Integrios.Infrastructure.OperatorKeys;
using Integrios.Infrastructure.Connections;
using Integrios.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Integrios.FunctionalTests.Infrastructure;

public sealed class ConnectionUsageTests(PostgresApiFixture fixture)
    : IClassFixture<PostgresApiFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetDataAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task OperatorKeyLookup_FindsActiveGlobalKey()
    {
        await using (var connection = fixture.CreateConnection())
        {
            await connection.OpenAsync();
            await connection.ExecuteAsync(
                """
                INSERT INTO operator_keys (public_key, secret_hash, name)
                VALUES ('global_operator_key', 'sha256:test', 'Test key')
                """);
        }

        await using IntegriosDbContext context = CreateContext();
        IOperatorKeyLookup repository = new OperatorKeyRepository(context);

        OperatorKey? key = await repository.FindActiveByPublicKeyAsync(
            "global_operator_key",
            CancellationToken.None);

        key.ShouldNotBeNull();
        key.SecretHash.ShouldBe("sha256:test");
    }

    [Fact]
    public async Task Usage_CountsActiveAssociations_AndIgnoresRetiredOnes()
    {
        Guid topicId = await fixture.SeedTopicAsync(fixture.TenantAId, "usage-topic");
        Guid connectionId = await fixture.SeedSourceConnectionAsync(fixture.TenantAId, "usage-source");

        await using IntegriosDbContext context = CreateContext();
        context.Sources.Add(new Source
        {
            Id = Guid.NewGuid(),
            TenantId = fixture.TenantAId,
            TopicId = topicId,
            ConnectionId = connectionId,
            Type = SourceType.EventApi,
            Configuration = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>("{\"source_contract\":\"event_json\"}"),
            Status = SourceStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await context.SaveChangesAsync();
        var repository = new ConnectionRepository(context);

        ConnectionUsage whileAssociated = await repository.GetUsageAsync(
            fixture.TenantAId, connectionId, CancellationToken.None);
        whileAssociated.Source.ShouldBeTrue();

        await context.Sources
            .Where(source => source.TenantId == fixture.TenantAId && source.ConnectionId == connectionId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(source => source.Status, SourceStatus.Revoked)
                .SetProperty(source => source.RevokedAt, DateTimeOffset.UtcNow));

        // The tombstone stays in the table for historical Event foreign keys, but a Connection no
        // longer associated with any Topic is not in source use and must not stay source-constrained
        // when it is next updated.
        ConnectionUsage afterRetirement = await repository.GetUsageAsync(
            fixture.TenantAId, connectionId, CancellationToken.None);
        afterRetirement.Source.ShouldBeFalse();
        afterRetirement.Destination.ShouldBeFalse();
    }

    private IntegriosDbContext CreateContext() => new(fixture.CreateOptions());
}
