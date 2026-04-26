using Integrios.Worker;

namespace Integrios.IntegrationTests;

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
    public async Task Replay_DeadLetteredDelivery_ResetsSubscriptionDeliveryToPending()
    {
        fixture.DeliveryClient.ShouldSucceed = false;
        var eventId = await fixture.InsertEventAndOutboxAsync("payment.created");

        for (var i = 1; i < OutboxWorker.MaxAttempts; i++)
        {
            await fixture.RunWorkerBatchAsync();
            await fixture.ForceDeliveryRetryNowAsync(eventId);
        }
        await fixture.RunWorkerBatchAsync();

        var deadDeliveries = await fixture.GetSubscriptionDeliveriesAsync(eventId);
        Assert.Single(deadDeliveries);
        Assert.Equal("dead_lettered", deadDeliveries[0].Status);

        var replayed = await fixture.ReplayAsync(eventId);

        Assert.True(replayed);
        var resetDeliveries = await fixture.GetSubscriptionDeliveriesAsync(eventId);
        Assert.Single(resetDeliveries);
        Assert.Equal("pending", resetDeliveries[0].Status);
        Assert.Equal(0, resetDeliveries[0].AttemptCount);
        Assert.Null(resetDeliveries[0].DeliverAfter);
    }

    [Fact]
    public async Task Replay_NoDeadLetteredDeliveries_ReturnsFalse()
    {
        var eventId = await fixture.InsertEventAndOutboxAsync("payment.created");
        await fixture.RunWorkerBatchAsync(); // succeeds — no failures to replay

        var replayed = await fixture.ReplayAsync(eventId);

        Assert.False(replayed);
    }

    [Fact]
    public async Task Replay_DeadLetteredDelivery_IsRedispatchedOnNextWorkerTick()
    {
        fixture.DeliveryClient.ShouldSucceed = false;
        var eventId = await fixture.InsertEventAndOutboxAsync("payment.created");

        for (var i = 1; i < OutboxWorker.MaxAttempts; i++)
        {
            await fixture.RunWorkerBatchAsync();
            await fixture.ForceDeliveryRetryNowAsync(eventId);
        }
        await fixture.RunWorkerBatchAsync();

        await fixture.ReplayAsync(eventId);

        fixture.DeliveryClient.Reset();
        fixture.DeliveryClient.ShouldSucceed = true;

        var dispatched = await fixture.RunWorkerBatchAsync();
        Assert.Equal(1, dispatched);
        Assert.Single(fixture.DeliveryClient.Calls);

        var deliveries = await fixture.GetSubscriptionDeliveriesAsync(eventId);
        Assert.Single(deliveries);
        Assert.Equal("succeeded", deliveries[0].Status);
    }
}
