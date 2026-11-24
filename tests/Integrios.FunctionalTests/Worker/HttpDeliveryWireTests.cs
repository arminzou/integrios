using System.Text.Json;
using Integrios.Domain.ValueObjects;
using WireMock;
using WireMock.Logging;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace Integrios.FunctionalTests.Worker;

/// What a Destination observes on the wire, through the production HTTP client.
public sealed class HttpDeliveryWireTests(HttpSinkFixture fixture) : IClassFixture<HttpSinkFixture>, IAsyncLifetime
{
    private const string LedgerPath = "/ledger";

    public async Task InitializeAsync()
    {
        await fixture.ResetAsync();
        await PointLedgerAtSinkAsync(authenticationJson: null);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task TransformedPayload_ArrivesAsTheTransformOutput()
    {
        StubLedger(Response.Create().WithStatusCode(200));
        await fixture.SetSubscriptionTransformByNameAsync(
            "to-ledger",
            """{"engine":"jsonata","version":"1","expression":"{ \"kind\": $context.event_type, \"topic\": $context.topic_name, \"test\": test }"}""");

        Guid eventId = await fixture.InsertEventAndOutboxAsync("payment.created");
        await fixture.RunWorkerBatchAsync();

        ILogEntry receipt = LedgerReceipts().ShouldHaveSingleItem();
        JsonElement.DeepEquals(
            JsonDocument.Parse(receipt.RequestMessage.Body!).RootElement,
            JsonDocument.Parse("""{"kind":"payment.created","topic":"test-topic","test":true}""").RootElement)
            .ShouldBeTrue(receipt.RequestMessage.Body);
        (await fixture.GetEventDeliveriesAsync(eventId)).ShouldHaveSingleItem().Status.ShouldBe("succeeded");
    }

    [Theory]
    [InlineData(
        """{"scheme":"api_key_header","config":{"header_name":"X-Api-Key"},"secret_refs":{"api_key":"wire-secret"}}""",
        "api_key_header", "X-Api-Key", "wire-secret-value")]
    [InlineData(
        """{"scheme":"bearer_token","config":{},"secret_refs":{"token":"wire-secret"}}""",
        "bearer_token", "Authorization", "Bearer wire-secret-value")]
    public async Task DestinationAuthentication_ReachesTheWire_AndTheSnapshotKeepsOnlyItsShape(
        string authenticationJson, string scheme, string header, string expectedValue)
    {
        fixture.SecretResolver.Set("wire-secret", "wire-secret-value");
        await PointLedgerAtSinkAsync(authenticationJson);
        StubLedger(Response.Create().WithStatusCode(200));

        Guid eventId = await fixture.InsertEventAndOutboxAsync("payment.created");
        await fixture.RunWorkerBatchAsync();

        LedgerReceipts().ShouldHaveSingleItem().RequestMessage.Headers![header].ShouldBe([expectedValue]);
        string snapshot = (await fixture.GetEventDeliverySnapshotAsync(eventId)).HttpExecutionSnapshotJson;
        snapshot.ShouldContain(scheme, Case.Sensitive);
        snapshot.ShouldNotContain("wire-secret-value", Case.Sensitive);
    }

    [Fact]
    public async Task Redirect_IsNotFollowed_AndFailsTheAttempt()
    {
        StubLedger(Response.Create().WithStatusCode(307).WithHeader("Location", fixture.SinkUrl("redirect-target")));
        fixture.Sink.Given(Request.Create().WithPath("/redirect-target"))
            .RespondWith(Response.Create().WithStatusCode(200));

        Guid eventId = await fixture.InsertEventAndOutboxAsync("payment.created");
        await fixture.RunWorkerBatchAsync();

        var attempt = (await fixture.GetEventDetailsAsync(eventId))!.DeliveryAttempts.ShouldHaveSingleItem();
        attempt.ResponseStatusCode.ShouldBe(307);
        attempt.FailurePhase.ShouldBe("http");
        (await fixture.GetEventDeliveriesAsync(eventId)).ShouldHaveSingleItem().Status.ShouldNotBe("succeeded");
        fixture.Sink.LogEntries.ShouldNotContain(entry => entry.RequestMessage.Path == "/redirect-target");
    }

    [Fact]
    public async Task SlowDestination_TimesOutOnTheClient()
    {
        StubLedger(Response.Create().WithStatusCode(200).WithDelay(HttpSinkFixture.HttpTimeout * 2));

        Guid eventId = await fixture.InsertEventAndOutboxAsync("payment.created");
        await fixture.RunWorkerBatchAsync();

        var attempt = (await fixture.GetEventDetailsAsync(eventId))!.DeliveryAttempts.ShouldHaveSingleItem();
        attempt.FailurePhase.ShouldBe("http");
        attempt.ErrorMessage.ShouldBe("Request timed out");
    }

    [Fact]
    public async Task SuccessRule_RejectingA200Body_DeadLettersWithoutRetrying()
    {
        await fixture.UpdateLedgerHttpSuccessAsync(new HttpSuccessRule { Evaluator = "json_boolean", Field = "ok", Expected = true });
        StubLedger(Response.Create().WithStatusCode(200).WithBodyAsJson(new { ok = false, error = "channel_not_found" }));

        Guid eventId = await fixture.InsertEventAndOutboxAsync("payment.created");
        await fixture.RunWorkerBatchAsync();

        var delivery = (await fixture.GetEventDeliveriesAsync(eventId)).ShouldHaveSingleItem();
        delivery.Status.ShouldBe("dead_lettered");
        delivery.LifetimeAttemptCount.ShouldBe(1);
    }

    [Fact]
    public async Task SuccessRule_AcceptingA200Body_Succeeds()
    {
        await fixture.UpdateLedgerHttpSuccessAsync(new HttpSuccessRule { Evaluator = "json_boolean", Field = "ok", Expected = true });
        StubLedger(Response.Create().WithStatusCode(200).WithBodyAsJson(new { ok = true }));

        Guid eventId = await fixture.InsertEventAndOutboxAsync("payment.created");
        await fixture.RunWorkerBatchAsync();

        (await fixture.GetEventDeliveriesAsync(eventId)).ShouldHaveSingleItem().Status.ShouldBe("succeeded");
    }

    [Fact]
    public async Task Throttled_RetryAfterIsHonoredOverTheDefaultBackoff()
    {
        StubLedger(Response.Create().WithStatusCode(429).WithHeader("Retry-After", "2"));

        Guid eventId = await fixture.InsertEventAndOutboxAsync("payment.created");
        await fixture.RunWorkerBatchAsync();

        var delivery = (await fixture.GetEventDeliveriesAsync(eventId)).ShouldHaveSingleItem();
        delivery.Status.ShouldBe("pending");
        DateTimeOffset completedAt = (await fixture.GetDeliveryAttemptsAsync(delivery.Id))
            .ShouldHaveSingleItem().CompletedAt.ShouldNotBeNull();
        // The default backoff would put the retry about 30 seconds out; 10 seconds separates the two.
        DateTimeOffset deliverAfter = delivery.DeliverAfter.ShouldNotBeNull();
        deliverAfter.ShouldBeGreaterThanOrEqualTo(completedAt.AddSeconds(1));
        deliverAfter.ShouldBeLessThanOrEqualTo(completedAt.AddSeconds(10));
    }

    [Fact]
    public async Task InterruptionAfterSend_RedeliversTheSameDeliveryAsANewAttempt()
    {
        StubLedger(Response.Create().WithStatusCode(200));
        Guid deliveryId = await fixture.FanoutSingleDeliveryAsync();

        // The Destination receives the first request, then recording its outcome fails, as when
        // the Worker dies between sending and finalizing.
        await fixture.WithDeliveryFinalizationFailureAsync(async () =>
            await fixture.RunDeliveryBatchAsync());
        await fixture.ForceLeaseExpiredAsync(deliveryId);
        await fixture.RunDeliveryBatchAsync();

        IReadOnlyList<DeliveryAttemptState> attempts = await fixture.GetDeliveryAttemptsAsync(deliveryId);
        attempts.Select(attempt => attempt.Status).ShouldBe(["indeterminate", "succeeded"]);
        IReadOnlyList<ILogEntry> receipts = LedgerReceipts();
        receipts.Count.ShouldBe(2);
        for (int index = 0; index < 2; index++)
        {
            IDictionary<string, WireMock.Types.WireMockList<string>> headers = receipts[index].RequestMessage.Headers!;
            headers["Integrios-Delivery-Id"].ShouldBe([deliveryId.ToString()]);
            headers["Integrios-Attempt-Id"].ShouldBe([attempts[index].Id.ToString()]);
            headers["Integrios-Attempt-Number"].ShouldBe([(index + 1).ToString()]);
        }
    }

    [Fact]
    public async Task Fanout_ProceedsWhileADeliveryAttemptIsInFlight()
    {
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        StubLedger(Response.Create().WithCallback(_ =>
        {
            arrived.TrySetResult();
            release.Wait(HttpSinkFixture.HttpTimeout);
            return new ResponseMessage { StatusCode = 200 };
        }));
        Guid blockedDelivery = await fixture.FanoutSingleDeliveryAsync();

        Task<int> delivering = fixture.RunDeliveryBatchAsync();
        await arrived.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Guid nextEvent = await fixture.InsertEventAndOutboxAsync("payment.created");
        int fannedOut = await fixture.RunFanoutBatchAsync();
        bool deliveryStillBlocked = !delivering.IsCompleted;
        release.Set();
        await delivering;

        fannedOut.ShouldBe(1);
        deliveryStillBlocked.ShouldBeTrue("Fanout waited for the in-flight delivery attempt.");
        (await fixture.GetEventDeliveriesAsync(nextEvent)).ShouldHaveSingleItem();
        (await fixture.GetEventDeliveryAsync(blockedDelivery)).Status.ShouldBe("succeeded");
    }

    private Task PointLedgerAtSinkAsync(string? authenticationJson) =>
        fixture.UpdateLedgerExecutionConfigurationAsync(fixture.SinkUrl(LedgerPath), authenticationJson, "webhook");

    private void StubLedger(IResponseBuilder response) =>
        fixture.Sink.Given(Request.Create().WithPath(LedgerPath).UsingPost()).RespondWith(response);

    private IReadOnlyList<ILogEntry> LedgerReceipts() =>
        fixture.Sink.LogEntries.Where(entry => entry.RequestMessage.Path == LedgerPath).ToList();
}
