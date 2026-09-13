namespace Integrios.FunctionalTests.Worker;

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
        details.ShouldNotBeNull();
        var attempt = details.DeliveryAttempts.ShouldHaveSingleItem();
        var delivery = (await fixture.GetEventDeliveriesAsync(eventId)).ShouldHaveSingleItem();
        attempt.AttemptNumber.ShouldBe(1);
        attempt.EventDeliveryId.ShouldBe(delivery.Id);
        attempt.Status.ShouldBe("succeeded");
        attempt.ResponseStatusCode.ShouldBe(200);
        attempt.ErrorMessage.ShouldBeNull();
        attempt.StartedAt.ShouldNotBe(default);
        attempt.CompletedAt.ShouldNotBeNull();
    }

    // The provider-specific UPDATE and the migrated column, exercised on whichever provider the run
    // is configured for. A body is diagnosis material, so it has to survive the write, not just the
    // client that produced it.
    [Fact]
    public async Task Delivery_PersistsTheCapturedResponseBody()
    {
        const string body = """{"error":"upstream unavailable"}""";
        fixture.DeliveryClient.ShouldSucceed = false;
        fixture.DeliveryClient.ResponseBody = body;

        var eventId = await fixture.InsertEventAndOutboxAsync("payment.created");
        await fixture.RunWorkerBatchAsync();

        var delivery = (await fixture.GetEventDeliveriesAsync(eventId)).ShouldHaveSingleItem();
        var attempt = (await fixture.GetDeliveryAttemptsAsync(delivery.Id)).ShouldHaveSingleItem();
        attempt.ResponseBody.ShouldBe(body);
        attempt.ResponseBodyTruncated.ShouldBeFalse();
    }

    [Fact]
    public async Task SuccessfulDelivery_PersistsItsResponseBodyToo()
    {
        const string body = """{"received":true}""";
        fixture.DeliveryClient.ResponseBody = body;

        var eventId = await fixture.InsertEventAndOutboxAsync("payment.created");
        await fixture.RunWorkerBatchAsync();

        var delivery = (await fixture.GetEventDeliveriesAsync(eventId)).ShouldHaveSingleItem();
        var attempt = (await fixture.GetDeliveryAttemptsAsync(delivery.Id)).ShouldHaveSingleItem();
        attempt.Status.ShouldBe("succeeded");
        attempt.ResponseBody.ShouldBe(body);
    }

    [Fact]
    public async Task TruncatedResponseBody_IsRecordedAsTruncated()
    {
        fixture.DeliveryClient.ResponseBody = new string('a', 32);
        fixture.DeliveryClient.ResponseBodyTruncated = true;

        var eventId = await fixture.InsertEventAndOutboxAsync("payment.created");
        await fixture.RunWorkerBatchAsync();

        var delivery = (await fixture.GetEventDeliveriesAsync(eventId)).ShouldHaveSingleItem();
        var attempt = (await fixture.GetDeliveryAttemptsAsync(delivery.Id)).ShouldHaveSingleItem();
        attempt.ResponseBodyTruncated.ShouldBeTrue();
    }

    [Fact]
    public async Task FailedDelivery_RecordsOneAttempt_WithFailedStatusAndErrorInfo()
    {
        fixture.DeliveryClient.ShouldSucceed = false;
        var eventId = await fixture.InsertEventAndOutboxAsync("payment.created");

        await fixture.RunWorkerBatchAsync();

        var details = await fixture.GetEventDetailsAsync(eventId);
        details.ShouldNotBeNull();
        var attempt = details.DeliveryAttempts.ShouldHaveSingleItem();
        attempt.AttemptNumber.ShouldBe(1);
        attempt.Status.ShouldBe("failed");
        attempt.FailurePhase.ShouldBe("http");
        attempt.ResponseStatusCode.ShouldBe(500);
        attempt.StartedAt.ShouldNotBe(default);
        attempt.CompletedAt.ShouldNotBeNull();
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
        details.ShouldNotBeNull();
        details.DeliveryAttempts.Count.ShouldBe(2);

        var first = details.DeliveryAttempts.Single(a => a.AttemptNumber == 1);
        first.Status.ShouldBe("failed");

        var second = details.DeliveryAttempts.Single(a => a.AttemptNumber == 2);
        second.Status.ShouldBe("succeeded");
        second.ResponseStatusCode.ShouldBe(200);
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
        details.ShouldNotBeNull();

        var attemptNumbers = details.DeliveryAttempts.Select(a => a.AttemptNumber).ToList();
        attemptNumbers.ShouldBe(attemptNumbers.OrderBy(n => n).ToList());
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

        DeliveryCall call = fixture.DeliveryClient.Calls.ShouldHaveSingleItem();
        var details = await fixture.GetEventDetailsAsync(eventId);
        details.ShouldNotBeNull();
        var attempt = details.DeliveryAttempts.ShouldHaveSingleItem();
        call.Headers["Integrios-Event-Id"].ShouldBe(eventId.ToString());
        call.Headers["Integrios-Delivery-Id"].ShouldBe(attempt.EventDeliveryId.ToString());
        call.Headers["Integrios-Attempt-Id"].ShouldBe(attempt.AttemptId.ToString());
        call.Headers["Integrios-Attempt-Number"].ShouldBe(attempt.AttemptNumber.ToString());
        call.Headers["Integrios-Delivery-Id"].ShouldNotBe("must-not-win");
    }
}
