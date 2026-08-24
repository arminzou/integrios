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
        Assert.Single(deadDeliveries);
        Assert.Equal("dead_lettered", deadDeliveries[0].Status);

        var replayed = await fixture.ReplayAsync(eventId, deadDeliveries[0].Id);

        Assert.Equal(DeadLetterReplayResult.Replayed, replayed);
        var resetDeliveries = await fixture.GetEventDeliveriesAsync(eventId);
        Assert.Single(resetDeliveries);
        Assert.Equal("pending", resetDeliveries[0].Status);
        Assert.Equal(RetryPolicy.DefaultMaxAttempts, resetDeliveries[0].LifetimeAttemptCount);
        Assert.Equal(0, resetDeliveries[0].RetryCycleAttemptCount);
        Assert.Null(resetDeliveries[0].DeliverAfter);
    }

    [Fact]
    public async Task Replay_NoDeadLetteredDeliveries_ReturnsFalse()
    {
        var eventId = await fixture.InsertEventAndOutboxAsync("payment.created");
        await fixture.RunWorkerBatchAsync(); // succeeds — no failures to replay

        var delivery = Assert.Single(await fixture.GetEventDeliveriesAsync(eventId));
        var replayed = await fixture.ReplayAsync(eventId, delivery.Id);

        Assert.Equal(DeadLetterReplayResult.NotDeadLettered, replayed);
    }

    [Fact]
    public async Task Replay_NonExistentEvent_ReturnsFalse()
    {
        var nonExistentEventId = Guid.NewGuid();
        var replayed = await fixture.ReplayAsync(nonExistentEventId, Guid.NewGuid());
        Assert.Equal(DeadLetterReplayResult.NotFound, replayed);
    }

    [Fact]
    public async Task Replay_EventOwnedByOtherTenant_ReturnsFalse()
    {
        var eventId = await fixture.InsertOrphanEventAndOutboxAsync("payment.created");
        // The orphan Tenant's Event produces no deliveries. Replaying it as the main Tenant
        // must still return false because replay is Tenant-isolated.
        var replayed = await fixture.ReplayAsync(eventId, Guid.NewGuid());
        Assert.Equal(DeadLetterReplayResult.NotFound, replayed);
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

        var delivery = Assert.Single(await fixture.GetEventDeliveriesAsync(eventId));
        Assert.Equal(DeadLetterReplayResult.Replayed, await fixture.ReplayAsync(eventId, delivery.Id));

        fixture.DeliveryClient.Reset();
        fixture.DeliveryClient.ShouldSucceed = true;

        var dispatched = await fixture.RunWorkerBatchAsync();
        Assert.Equal(1, dispatched);
        Assert.Single(fixture.DeliveryClient.Calls);

        var deliveries = await fixture.GetEventDeliveriesAsync(eventId);
        Assert.Single(deliveries);
        Assert.Equal("succeeded", deliveries[0].Status);
    }
}
