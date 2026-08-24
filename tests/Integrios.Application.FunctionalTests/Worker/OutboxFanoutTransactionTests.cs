using System.Data.Common;
using Dapper;
using Integrios.Application.Delivery;
using Integrios.Domain.Entities;
using Integrios.Domain.Enums;

namespace Integrios.Application.FunctionalTests.Worker;

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

        await ConsistencyContractAssertions.FanoutProcessesOnceAsync(
            eventId,
            processedCounts,
            fixture.GetSubscriptionDeliveryCountAsync,
            fixture.IsOutboxRowProcessedAsync,
            fixture.GetEventStatusAsync);
    }

    [Fact]
    public async Task OutboxRowLockedByAnotherOwner_IsSkippedUntilTheOwnerRollsBack()
    {
        var eventId = await fixture.InsertEventAndOutboxAsync("payment.created");

        await using var ownerConnection = fixture.CreateConnection();
        await ownerConnection.OpenAsync();
        await using var ownerTransaction = await ownerConnection.BeginTransactionAsync();
        Assert.True(await fixture.LockOutboxRowAsync(ownerConnection, ownerTransaction, eventId));

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

        await fixture.WithOutboxCompletionFailureAsync(async () =>
        {
            await Assert.ThrowsAnyAsync<DbException>(() => fixture.RunFanoutBatchAsync(1));

            Assert.False(await fixture.IsOutboxRowProcessedAsync(eventId));
            Assert.Equal("accepted", await fixture.GetEventStatusAsync(eventId));
            Assert.Equal(0, await fixture.GetSubscriptionDeliveryCountAsync(eventId));
        });

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
            while (await fixture.DeliveryQueue.ClaimNextAsync(CancellationToken.None) is { } claimed)
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
                        null),
                    CancellationToken.None);
                Assert.Equal(DeliveryFinalizationStatus.Applied, result.Status);
                Assert.Equal(SubscriptionDeliveryDisposition.Succeeded, result.Disposition);
                total++;
            }
            return total;
        }
    }

    private async Task<T> ScalarAsync<T>(string sql)
    {
        await using var connection = fixture.CreateConnection();
        await connection.OpenAsync();
        object value = await connection.ExecuteScalarAsync(sql)
            ?? throw new InvalidOperationException($"Query returned no value: {sql}");
        return (T)Convert.ChangeType(value, typeof(T));
    }
}
