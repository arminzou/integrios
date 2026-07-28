using Integrios.Application.Abstractions;
using Integrios.Application.Delivery;
using Integrios.Domain.Delivery;
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

    [Fact]
    public async Task TwoExecutionLoops_BoundedStressLosesNoWorkAndCreatesNoDuplicates()
    {
        const int eventCount = 50;
        for (int index = 0; index < eventCount; index++)
            await fixture.InsertEventAndOutboxAsync("payment.created");

        int[] fanoutCounts = await Task.WhenAll(FanoutUntilEmptyAsync(), FanoutUntilEmptyAsync())
            .WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(eventCount, fanoutCounts.Sum());
        Assert.Equal(eventCount, await ScalarAsync<long>("SELECT COUNT(*) FROM outbox WHERE processed_at IS NOT NULL"));
        Assert.Equal(eventCount, await ScalarAsync<long>("SELECT COUNT(*) FROM events WHERE status = 'fanned_out'"));
        Assert.Equal(eventCount, await ScalarAsync<long>("SELECT COUNT(*) FROM subscription_deliveries"));
        Assert.Equal(0, await ScalarAsync<long>(
            "SELECT COUNT(*) FROM (SELECT event_id, subscription_id FROM subscription_deliveries GROUP BY event_id, subscription_id HAVING COUNT(*) > 1) duplicates"));

        int[] deliveryCounts = await Task.WhenAll(DeliverUntilEmptyAsync(), DeliverUntilEmptyAsync())
            .WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(eventCount, deliveryCounts.Sum());
        Assert.Equal(eventCount, await ScalarAsync<long>("SELECT COUNT(*) FROM subscription_deliveries WHERE status = 'succeeded'"));
        Assert.Equal(eventCount, await ScalarAsync<long>("SELECT COUNT(*) FROM delivery_attempts WHERE status = 'succeeded'"));
        Assert.Equal(0, await ScalarAsync<long>(
            "SELECT COUNT(*) FROM (SELECT subscription_delivery_id, attempt_number FROM delivery_attempts GROUP BY subscription_delivery_id, attempt_number HAVING COUNT(*) > 1) duplicates"));

        async Task<int> FanoutUntilEmptyAsync()
        {
            int total = 0;
            while (true)
            {
                int processed = await fixture.RunFanoutBatchAsync(10);
                total += processed;
                if (processed == 0)
                    return total;
            }
        }

        async Task<int> DeliverUntilEmptyAsync()
        {
            int total = 0;
            while (await fixture.DeliveryQueue.ClaimNextAsync() is { } claimed)
            {
                DeliveryFinalizationResult result = await fixture.DeliveryQueue.FinalizeAsync(
                    new DeliveryAttemptCompletion(
                        claimed.Id,
                        claimed.AttemptId,
                        true,
                        null,
                        claimed.PayloadJson,
                        200,
                        null,
                        null));
                Assert.Equal(DeliveryFinalizationStatus.Applied, result.Status);
                Assert.Equal(SubscriptionDeliveryDisposition.Succeeded, result.Disposition);
                total++;
            }
            return total;
        }
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<T> ScalarAsync<T>(string sql)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync()
            ?? throw new InvalidOperationException($"Query returned no value: {sql}"));
    }
}
