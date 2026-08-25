namespace Integrios.Application.FunctionalTests.Worker;

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

        dispatched.ShouldBe(2);
        fixture.DeliveryClient.Calls.Count.ShouldBe(2);
        fixture.DeliveryClient.Calls.ShouldContain(c => c.Url == WorkerRoutingFixture.LedgerSinkUrl);
        fixture.DeliveryClient.Calls.ShouldContain(c => c.Url == WorkerRoutingFixture.RiskSinkUrl);

        var deliveries = await fixture.GetEventDeliveriesAsync(eventId);
        deliveries.Count.ShouldBe(2);
        foreach (var d in deliveries)
            d.Status.ShouldBe("succeeded");

        (await fixture.IsOutboxRowProcessedAsync(eventId)).ShouldBeTrue();
    }

    [Fact]
    public async Task Worker_MultipleMatchingSubscriptions_AllFail_EachRetriesIndependently()
    {
        fixture.DeliveryClient.ShouldSucceed = false;
        var eventId = await fixture.InsertEventAndOutboxAsync("payment.multi");

        await fixture.RunWorkerBatchAsync();

        fixture.DeliveryClient.Calls.Count.ShouldBe(2);

        // Outbox is processed after Stage 1 regardless of per-subscription outcomes
        (await fixture.IsOutboxRowProcessedAsync(eventId)).ShouldBeTrue();

        // Each event_delivery has its own retry state — independent failure isolation
        var deliveries = await fixture.GetEventDeliveriesAsync(eventId);
        deliveries.Count.ShouldBe(2);
        foreach (var d in deliveries)
        {
            d.Status.ShouldBe("pending");
            d.AttemptCount.ShouldBe(1);
            d.DeliverAfter.ShouldNotBeNull();
        }
    }
}
