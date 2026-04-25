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
    public async Task Worker_MultipleMatchingRoutes_DeliversToAllSinks()
    {
        var eventId = await fixture.InsertEventAndOutboxAsync("payment.multi");

        var processed = await fixture.RunWorkerBatchAsync();

        Assert.Equal(1, processed);
        Assert.Equal(2, fixture.DeliveryClient.Calls.Count);
        Assert.Contains(fixture.DeliveryClient.Calls, c => c.Url == WorkerRoutingFixture.LedgerSinkUrl);
        Assert.Contains(fixture.DeliveryClient.Calls, c => c.Url == WorkerRoutingFixture.RiskSinkUrl);

        var status = await fixture.GetEventStatusAsync(eventId);
        Assert.Equal("completed", status);

        Assert.True(await fixture.IsOutboxRowProcessedAsync(eventId));
    }

    [Fact]
    public async Task Worker_MultipleMatchingRoutes_PartialFailure_DoesNotMarkCompleted()
    {
        // First delivery succeeds, second fails — event should not be marked completed
        fixture.DeliveryClient.ShouldSucceed = false;
        var eventId = await fixture.InsertEventAndOutboxAsync("payment.multi");

        await fixture.RunWorkerBatchAsync();

        Assert.Equal(2, fixture.DeliveryClient.Calls.Count);

        var status = await fixture.GetEventStatusAsync(eventId);
        Assert.NotEqual("completed", status);

        Assert.False(await fixture.IsOutboxRowProcessedAsync(eventId));
    }
}
