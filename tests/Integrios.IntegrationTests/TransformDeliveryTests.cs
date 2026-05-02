namespace Integrios.IntegrationTests;

public sealed class TransformDeliveryTests : IClassFixture<WorkerRoutingFixture>, IAsyncLifetime
{
    private readonly WorkerRoutingFixture fixture;

    public TransformDeliveryTests(WorkerRoutingFixture fixture)
    {
        this.fixture = fixture;
    }

    public async Task InitializeAsync() => await fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Worker_SubscriptionWithTransform_DeliveredPayloadIsTransformed()
    {
        // $.test on {"test":true} extracts the boolean, so the delivered payload differs from the original
        var transformJson = """{"engine":"jsonata","version":"1","expression":"$.test"}""";
        await fixture.SetSubscriptionTransformByNameAsync("to-ledger", transformJson);

        var eventId = await fixture.InsertEventAndOutboxAsync("payment.created");
        await fixture.RunWorkerBatchAsync();

        Assert.Single(fixture.DeliveryClient.Calls);
        var deliveredPayload = fixture.DeliveryClient.Calls[0].Payload;

        // The transform was applied: result should not be the raw original payload
        Assert.NotEqual("{\"test\":true}", deliveredPayload);
        Assert.False(string.IsNullOrWhiteSpace(deliveredPayload));

        var deliveries = await fixture.GetSubscriptionDeliveriesAsync(eventId);
        Assert.Single(deliveries);
        Assert.Equal("succeeded", deliveries[0].Status);
    }

    [Fact]
    public async Task Worker_SubscriptionWithFailingTransform_DeliverySchedulesRetry()
    {
        // $error() is valid JSONata syntax but throws at evaluation time
        var transformJson = """{"engine":"jsonata","version":"1","expression":"$error(\"forced\")"}""";
        await fixture.SetSubscriptionTransformByNameAsync("to-ledger", transformJson);

        var eventId = await fixture.InsertEventAndOutboxAsync("payment.created");
        await fixture.RunWorkerBatchAsync();

        // Transform failure is treated as delivery failure: no HTTP call made
        Assert.Empty(fixture.DeliveryClient.Calls);

        var deliveries = await fixture.GetSubscriptionDeliveriesAsync(eventId);
        Assert.Single(deliveries);
        Assert.Equal("pending", deliveries[0].Status);
        Assert.Equal(1, deliveries[0].AttemptCount);
        Assert.NotNull(deliveries[0].DeliverAfter);
    }
}
