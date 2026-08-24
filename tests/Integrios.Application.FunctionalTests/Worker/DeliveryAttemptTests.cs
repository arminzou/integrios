namespace Integrios.Application.FunctionalTests.Worker;

public sealed class DeliveryAttemptTests : IClassFixture<WorkerRoutingFixture>, IAsyncLifetime
{
    private readonly WorkerRoutingFixture fixture;

    public DeliveryAttemptTests(WorkerRoutingFixture fixture)
    {
        this.fixture = fixture;
    }

    public async Task InitializeAsync() => await fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task SuccessfulDelivery_RecordsOneAttempt_WithSucceededStatusAndStatusCode()
    {
        var eventId = await fixture.InsertEventAndOutboxAsync("payment.created");

        await fixture.RunWorkerBatchAsync();

        var details = await fixture.GetEventDetailsAsync(eventId);
        Assert.NotNull(details);
        var attempt = Assert.Single(details.DeliveryAttempts);
        var delivery = Assert.Single(await fixture.GetEventDeliveriesAsync(eventId));
        Assert.Equal(1, attempt.AttemptNumber);
        Assert.Equal(delivery.Id, attempt.EventDeliveryId);
        Assert.Equal("succeeded", attempt.Status);
        Assert.Equal(200, attempt.ResponseStatusCode);
        Assert.Null(attempt.ErrorMessage);
        Assert.NotEqual(default, attempt.StartedAt);
        Assert.NotNull(attempt.CompletedAt);
    }

    [Fact]
    public async Task FailedDelivery_RecordsOneAttempt_WithFailedStatusAndErrorInfo()
    {
        fixture.DeliveryClient.ShouldSucceed = false;
        var eventId = await fixture.InsertEventAndOutboxAsync("payment.created");

        await fixture.RunWorkerBatchAsync();

        var details = await fixture.GetEventDetailsAsync(eventId);
        Assert.NotNull(details);
        var attempt = Assert.Single(details.DeliveryAttempts);
        Assert.Equal(1, attempt.AttemptNumber);
        Assert.Equal("failed", attempt.Status);
        Assert.Equal("http", attempt.FailurePhase);
        Assert.Equal(500, attempt.ResponseStatusCode);
        Assert.NotEqual(default, attempt.StartedAt);
        Assert.NotNull(attempt.CompletedAt);
    }

    [Fact]
    public async Task RetryAfterFailure_RecordsTwoAttempts_WithCorrectAttemptNumbers()
    {
        fixture.DeliveryClient.ShouldSucceed = false;
        var eventId = await fixture.InsertEventAndOutboxAsync("payment.created");

        // First attempt — fails
        await fixture.RunWorkerBatchAsync();

        // Force retry and succeed on second attempt
        await fixture.ForceDeliveryRetryNowAsync(eventId);
        fixture.DeliveryClient.ShouldSucceed = true;
        await fixture.RunWorkerBatchAsync();

        var details = await fixture.GetEventDetailsAsync(eventId);
        Assert.NotNull(details);
        Assert.Equal(2, details.DeliveryAttempts.Count);

        var first = details.DeliveryAttempts.Single(a => a.AttemptNumber == 1);
        Assert.Equal("failed", first.Status);

        var second = details.DeliveryAttempts.Single(a => a.AttemptNumber == 2);
        Assert.Equal("succeeded", second.Status);
        Assert.Equal(200, second.ResponseStatusCode);
    }

    [Fact]
    public async Task GetEventById_ReturnsDeliveryAttempts_InAttemptOrder()
    {
        fixture.DeliveryClient.ShouldSucceed = false;
        var eventId = await fixture.InsertEventAndOutboxAsync("payment.created");

        await fixture.RunWorkerBatchAsync();
        await fixture.ForceDeliveryRetryNowAsync(eventId);
        await fixture.RunWorkerBatchAsync();
        await fixture.ForceDeliveryRetryNowAsync(eventId);
        await fixture.RunWorkerBatchAsync();

        var details = await fixture.GetEventDetailsAsync(eventId);
        Assert.NotNull(details);

        var attemptNumbers = details.DeliveryAttempts.Select(a => a.AttemptNumber).ToList();
        Assert.Equal(attemptNumbers.OrderBy(n => n).ToList(), attemptNumbers);
    }

    [Fact]
    public async Task OutboundRequest_UsesStableDeliveryAndDiagnosticAttemptHeaders_OverAuthConfiguration()
    {
        const string reservedOverrideSecret = "reserved_override";
        fixture.SecretResolver.Set(reservedOverrideSecret, "must-not-win");
        await fixture.UpdateLedgerExecutionConfigurationAsync(
            WorkerRoutingFixture.LedgerSinkUrl,
            """{"scheme":"api_key_header","config":{"header_name":"Integrios-Delivery-Id"},"secret_refs":{"api_key":"reserved_override"}}""",
            "webhook");
        Guid eventId = await fixture.InsertEventAndOutboxAsync("payment.created");

        await fixture.RunWorkerBatchAsync();

        DeliveryCall call = Assert.Single(fixture.DeliveryClient.Calls);
        var details = await fixture.GetEventDetailsAsync(eventId);
        Assert.NotNull(details);
        var attempt = Assert.Single(details.DeliveryAttempts);
        Assert.Equal(eventId.ToString(), call.Headers["Integrios-Event-Id"]);
        Assert.Equal(attempt.EventDeliveryId.ToString(), call.Headers["Integrios-Delivery-Id"]);
        Assert.Equal(attempt.AttemptId.ToString(), call.Headers["Integrios-Attempt-Id"]);
        Assert.Equal(attempt.AttemptNumber.ToString(), call.Headers["Integrios-Attempt-Number"]);
        Assert.NotEqual("must-not-win", call.Headers["Integrios-Delivery-Id"]);
    }
}
