namespace Integrios.IntegrationTests;

public sealed class MultiRouteDeliveryTests : IClassFixture<WorkerRoutingFixture>, IAsyncLifetime
{
    private readonly WorkerRoutingFixture fixture;

    public MultiRouteDeliveryTests(WorkerRoutingFixture fixture)
    {
        this.fixture = fixture;
    }

    public async Task InitializeAsync() => await fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Worker_MultipleMatchingSubscriptions_DeliversToAllSinks()
    {
        var eventId = await fixture.InsertEventAndOutboxAsync("payment.multi");

        var dispatched = await fixture.RunWorkerBatchAsync();

        Assert.Equal(2, dispatched);
        Assert.Equal(2, fixture.DeliveryClient.Calls.Count);
        Assert.Contains(fixture.DeliveryClient.Calls, c => c.Url == WorkerRoutingFixture.LedgerSinkUrl);
        Assert.Contains(fixture.DeliveryClient.Calls, c => c.Url == WorkerRoutingFixture.RiskSinkUrl);

        var deliveries = await fixture.GetSubscriptionDeliveriesAsync(eventId);
        Assert.Equal(2, deliveries.Count);
        Assert.All(deliveries, d => Assert.Equal("succeeded", d.Status));

        Assert.True(await fixture.IsOutboxRowProcessedAsync(eventId));
    }

    [Fact]
    public async Task Worker_MultipleMatchingSubscriptions_AllFail_EachRetriesIndependently()
    {
        fixture.DeliveryClient.ShouldSucceed = false;
        var eventId = await fixture.InsertEventAndOutboxAsync("payment.multi");

        await fixture.RunWorkerBatchAsync();

        Assert.Equal(2, fixture.DeliveryClient.Calls.Count);

        // Outbox is processed after Stage 1 regardless of per-subscription outcomes
        Assert.True(await fixture.IsOutboxRowProcessedAsync(eventId));

        // Each subscription_delivery has its own retry state — independent failure isolation
        var deliveries = await fixture.GetSubscriptionDeliveriesAsync(eventId);
        Assert.Equal(2, deliveries.Count);
        Assert.All(deliveries, d =>
        {
            Assert.Equal("pending", d.Status);
            Assert.Equal(1, d.AttemptCount);
            Assert.NotNull(d.DeliverAfter);
        });
    }
}
