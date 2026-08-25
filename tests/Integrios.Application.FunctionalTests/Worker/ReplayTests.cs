using Integrios.Application.Delivery;

namespace Integrios.Application.FunctionalTests.Worker;

public sealed class ReplayTests : IClassFixture<WorkerRoutingFixture>, IAsyncLifetime
{
    private readonly WorkerRoutingFixture fixture;

    public ReplayTests(WorkerRoutingFixture fixture)
    {
        this.fixture = fixture;
    }

    public async Task InitializeAsync() => await fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Replay_DeadLetteredDelivery_ResetsEventDeliveryToPending()
    {
        fixture.DeliveryClient.ShouldSucceed = false;
        var eventId = await fixture.InsertEventAndOutboxAsync("payment.created");

        for (var i = 1; i < RetryPolicy.DefaultMaxAttempts; i++)
        {
            await fixture.RunWorkerBatchAsync();
            await fixture.ForceDeliveryRetryNowAsync(eventId);
        }
        await fixture.RunWorkerBatchAsync();

        var deadDeliveries = await fixture.GetEventDeliveriesAsync(eventId);
        deadDeliveries.ShouldHaveSingleItem();
        deadDeliveries[0].Status.ShouldBe("dead_lettered");

        var replayed = await fixture.ReplayAsync(eventId, deadDeliveries[0].Id);

        replayed.ShouldBe(DeadLetterReplayResult.Replayed);
        var resetDeliveries = await fixture.GetEventDeliveriesAsync(eventId);
        resetDeliveries.ShouldHaveSingleItem();
        resetDeliveries[0].Status.ShouldBe("pending");
        resetDeliveries[0].LifetimeAttemptCount.ShouldBe(RetryPolicy.DefaultMaxAttempts);
        resetDeliveries[0].RetryCycleAttemptCount.ShouldBe(0);
        resetDeliveries[0].DeliverAfter.ShouldBeNull();
    }

    [Fact]
    public async Task Replay_NoDeadLetteredDeliveries_ReturnsFalse()
    {
        var eventId = await fixture.InsertEventAndOutboxAsync("payment.created");
        await fixture.RunWorkerBatchAsync(); // succeeds — no failures to replay

        var delivery = (await fixture.GetEventDeliveriesAsync(eventId)).ShouldHaveSingleItem();
        var replayed = await fixture.ReplayAsync(eventId, delivery.Id);

        replayed.ShouldBe(DeadLetterReplayResult.NotDeadLettered);
    }

    [Fact]
    public async Task Replay_NonExistentEvent_ReturnsFalse()
    {
        var nonExistentEventId = Guid.NewGuid();
        var replayed = await fixture.ReplayAsync(nonExistentEventId, Guid.NewGuid());
        replayed.ShouldBe(DeadLetterReplayResult.NotFound);
    }

    [Fact]
    public async Task Replay_EventOwnedByOtherTenant_ReturnsFalse()
    {
        var eventId = await fixture.InsertOrphanEventAndOutboxAsync("payment.created");
        // The orphan Tenant's Event produces no deliveries. Replaying it as the main Tenant
        // must still return false because replay is Tenant-isolated.
        var replayed = await fixture.ReplayAsync(eventId, Guid.NewGuid());
        replayed.ShouldBe(DeadLetterReplayResult.NotFound);
    }

    [Fact]
    public async Task Replay_DeadLetteredDelivery_IsRedispatchedOnNextWorkerTick()
    {
        fixture.DeliveryClient.ShouldSucceed = false;
        var eventId = await fixture.InsertEventAndOutboxAsync("payment.created");

        for (var i = 1; i < RetryPolicy.DefaultMaxAttempts; i++)
        {
            await fixture.RunWorkerBatchAsync();
            await fixture.ForceDeliveryRetryNowAsync(eventId);
        }
        await fixture.RunWorkerBatchAsync();

        var delivery = (await fixture.GetEventDeliveriesAsync(eventId)).ShouldHaveSingleItem();
        (await fixture.ReplayAsync(eventId, delivery.Id)).ShouldBe(DeadLetterReplayResult.Replayed);

        fixture.DeliveryClient.Reset();
        fixture.DeliveryClient.ShouldSucceed = true;

        var dispatched = await fixture.RunWorkerBatchAsync();
        dispatched.ShouldBe(1);
        fixture.DeliveryClient.Calls.ShouldHaveSingleItem();

        var deliveries = await fixture.GetEventDeliveriesAsync(eventId);
        deliveries.ShouldHaveSingleItem();
        deliveries[0].Status.ShouldBe("succeeded");
    }
}
