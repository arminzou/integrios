using Npgsql;

namespace Integrios.IntegrationTests;

public sealed class OutboxFanoutTransactionTests : IClassFixture<WorkerRoutingFixture>, IAsyncLifetime
{
    private readonly WorkerRoutingFixture fixture;

    public OutboxFanoutTransactionTests(WorkerRoutingFixture fixture)
    {
        this.fixture = fixture;
    }

    public Task InitializeAsync() => fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task ConcurrentProcessors_CreateOneDeliveryAndOnlyOneProcessesTheOutboxRow()
    {
        var eventId = await fixture.InsertEventAndOutboxAsync("payment.created");

        var processedCounts = await Task.WhenAll(
            fixture.RunFanoutBatchAsync(1),
            fixture.RunFanoutBatchAsync(1));

        Assert.Equal(1, processedCounts.Sum());
        Assert.Equal(1, await fixture.GetSubscriptionDeliveryCountAsync(eventId));
        Assert.True(await fixture.IsOutboxRowProcessedAsync(eventId));
        Assert.Equal("fanned_out", await fixture.GetEventStatusAsync(eventId));
    }

    [Fact]
    public async Task OutboxRowLockedByAnotherOwner_IsSkippedUntilTheOwnerRollsBack()
    {
        var eventId = await fixture.InsertEventAndOutboxAsync("payment.created");

        await using var ownerConnection = new NpgsqlConnection(fixture.ConnectionString);
        await ownerConnection.OpenAsync();
        await using var ownerTransaction = await ownerConnection.BeginTransactionAsync();
        await using (var lockCommand = new NpgsqlCommand(
            "SELECT id FROM outbox WHERE event_id = @EventId FOR UPDATE",
            ownerConnection,
            ownerTransaction))
        {
            lockCommand.Parameters.AddWithValue("EventId", eventId);
            Assert.NotNull(await lockCommand.ExecuteScalarAsync());
        }

        Assert.Equal(0, await fixture.RunFanoutBatchAsync(1));
        Assert.False(await fixture.IsOutboxRowProcessedAsync(eventId));
        Assert.Equal(0, await fixture.GetSubscriptionDeliveryCountAsync(eventId));

        await ownerTransaction.RollbackAsync();

        Assert.Equal(1, await fixture.RunFanoutBatchAsync(1));
        Assert.True(await fixture.IsOutboxRowProcessedAsync(eventId));
        Assert.Equal(1, await fixture.GetSubscriptionDeliveryCountAsync(eventId));
    }

    [Fact]
    public async Task CompletionFailure_RollsBackFanoutAndEventStatus_ThenRowIsImmediatelyReclaimable()
    {
        var eventId = await fixture.InsertEventAndOutboxAsync("payment.created");

        await ExecuteAsync(
            """
            CREATE FUNCTION fail_outbox_completion() RETURNS trigger AS $$
            BEGIN
                IF OLD.processed_at IS NULL AND NEW.processed_at IS NOT NULL THEN
                    RAISE EXCEPTION 'simulated interruption before outbox completion';
                END IF;
                RETURN NEW;
            END;
            $$ LANGUAGE plpgsql;

            CREATE TRIGGER fail_outbox_completion
            BEFORE UPDATE ON outbox
            FOR EACH ROW EXECUTE FUNCTION fail_outbox_completion();
            """);

        try
        {
            await Assert.ThrowsAsync<PostgresException>(() => fixture.RunFanoutBatchAsync(1));

            Assert.False(await fixture.IsOutboxRowProcessedAsync(eventId));
            Assert.Equal("accepted", await fixture.GetEventStatusAsync(eventId));
            Assert.Equal(0, await fixture.GetSubscriptionDeliveryCountAsync(eventId));
        }
        finally
        {
            await ExecuteAsync(
                """
                DROP TRIGGER IF EXISTS fail_outbox_completion ON outbox;
                DROP FUNCTION IF EXISTS fail_outbox_completion();
                """);
        }

        Assert.Equal(1, await fixture.RunFanoutBatchAsync(1));
        Assert.True(await fixture.IsOutboxRowProcessedAsync(eventId));
        Assert.Equal("fanned_out", await fixture.GetEventStatusAsync(eventId));
        Assert.Equal(1, await fixture.GetSubscriptionDeliveryCountAsync(eventId));
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
