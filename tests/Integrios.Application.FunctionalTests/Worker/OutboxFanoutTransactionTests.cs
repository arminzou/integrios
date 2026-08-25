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
            fixture.GetEventDeliveryCountAsync,
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
        (await fixture.LockOutboxRowAsync(ownerConnection, ownerTransaction, eventId)).ShouldBeTrue();

        (await fixture.RunFanoutBatchAsync(1)).ShouldBe(0);
        (await fixture.IsOutboxRowProcessedAsync(eventId)).ShouldBeFalse();
        (await fixture.GetEventDeliveryCountAsync(eventId)).ShouldBe(0);

        await ownerTransaction.RollbackAsync();

        (await fixture.RunFanoutBatchAsync(1)).ShouldBe(1);
        (await fixture.IsOutboxRowProcessedAsync(eventId)).ShouldBeTrue();
        (await fixture.GetEventDeliveryCountAsync(eventId)).ShouldBe(1);
    }

    [Fact]
    public async Task CompletionFailure_RollsBackFanoutAndEventStatus_ThenRowIsImmediatelyReclaimable()
    {
        var eventId = await fixture.InsertEventAndOutboxAsync("payment.created");

        await fixture.WithOutboxCompletionFailureAsync(async () =>
        {
            await Should.ThrowAsync<DbException>(() => fixture.RunFanoutBatchAsync(1));

            (await fixture.IsOutboxRowProcessedAsync(eventId)).ShouldBeFalse();
            (await fixture.GetEventStatusAsync(eventId)).ShouldBe("accepted");
            (await fixture.GetEventDeliveryCountAsync(eventId)).ShouldBe(0);
        });

        (await fixture.RunFanoutBatchAsync(1)).ShouldBe(1);
        (await fixture.IsOutboxRowProcessedAsync(eventId)).ShouldBeTrue();
        (await fixture.GetEventStatusAsync(eventId)).ShouldBe("routed");
        (await fixture.GetEventDeliveryCountAsync(eventId)).ShouldBe(1);
    }

    [Fact]
    public async Task TwoExecutionLoops_BoundedStressLosesNoWorkAndCreatesNoDuplicates()
    {
        const int eventCount = 50;
        for (int index = 0; index < eventCount; index++)
            await fixture.InsertEventAndOutboxAsync("payment.created");

        int[] fanoutCounts = await Task.WhenAll(FanoutUntilEmptyAsync(), FanoutUntilEmptyAsync())
            .WaitAsync(TimeSpan.FromSeconds(30));

        fanoutCounts.Sum().ShouldBe(eventCount);
        (await ScalarAsync<long>("SELECT COUNT(*) FROM outbox WHERE processed_at IS NOT NULL")).ShouldBe(eventCount);
        (await ScalarAsync<long>("SELECT COUNT(*) FROM events WHERE status = 'routed'")).ShouldBe(eventCount);
        (await ScalarAsync<long>("SELECT COUNT(*) FROM event_deliveries")).ShouldBe(eventCount);
        (await ScalarAsync<long>(
            "SELECT COUNT(*) FROM (SELECT event_id, subscription_id FROM event_deliveries GROUP BY event_id, subscription_id HAVING COUNT(*) > 1) duplicates")).ShouldBe(0);

        int[] deliveryCounts = await Task.WhenAll(DeliverUntilEmptyAsync(), DeliverUntilEmptyAsync())
            .WaitAsync(TimeSpan.FromSeconds(30));

        deliveryCounts.Sum().ShouldBe(eventCount);
        (await ScalarAsync<long>("SELECT COUNT(*) FROM event_deliveries WHERE status = 'succeeded'")).ShouldBe(eventCount);
        (await ScalarAsync<long>("SELECT COUNT(*) FROM delivery_attempts WHERE status = 'succeeded'")).ShouldBe(eventCount);
        (await ScalarAsync<long>(
            "SELECT COUNT(*) FROM (SELECT event_delivery_id, attempt_number FROM delivery_attempts GROUP BY event_delivery_id, attempt_number HAVING COUNT(*) > 1) duplicates")).ShouldBe(0);

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
                result.Status.ShouldBe(DeliveryFinalizationStatus.Applied);
                result.Disposition.ShouldBe(EventDeliveryDisposition.Succeeded);
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
