using Integrios.Application.Delivery;

namespace Integrios.FunctionalTests.Worker;

public sealed class WorkerRoutingTests : IClassFixture<WorkerRoutingFixture>, IAsyncLifetime
{
    private readonly WorkerRoutingFixture fixture;

    public WorkerRoutingTests(WorkerRoutingFixture fixture)
    {
        this.fixture = fixture;
    }

    public async Task InitializeAsync() => await fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Worker_MatchingSubscription_DeliversEventAndMarksDeliverySucceeded()
    {
        var eventId = await fixture.InsertEventAndOutboxAsync("payment.created");

        var dispatched = await fixture.RunWorkerBatchAsync();

        dispatched.ShouldBe(1);
        fixture.DeliveryClient.Calls.ShouldHaveSingleItem();
        fixture.DeliveryClient.Calls[0].Url.ShouldBe(WorkerRoutingFixture.LedgerSinkUrl);

        var deliveries = await fixture.GetEventDeliveriesAsync(eventId);
        deliveries.ShouldHaveSingleItem();
        deliveries[0].Status.ShouldBe("succeeded");

        // Outbox row is always processed after Stage 1 fanout
        (await fixture.IsOutboxRowProcessedAsync(eventId)).ShouldBeTrue();
    }

    [Fact]
    public async Task Worker_SubscriptionMatchingSelectsByEventType_CorrectSinkReceivesDelivery()
    {
        var eventId = await fixture.InsertEventAndOutboxAsync("payment.authorized");

        await fixture.RunWorkerBatchAsync();

        fixture.DeliveryClient.Calls.ShouldHaveSingleItem();
        fixture.DeliveryClient.Calls[0].Url.ShouldBe(WorkerRoutingFixture.RiskSinkUrl);

        var deliveries = await fixture.GetEventDeliveriesAsync(eventId);
        deliveries.ShouldHaveSingleItem();
        deliveries[0].Status.ShouldBe("succeeded");
    }

    [Fact]
    public async Task Worker_NoMatchingSubscription_MarksUnroutedAndCompletesOutbox()
    {
        var eventId = await fixture.InsertEventAndOutboxAsync("unknown.event.type");

        var dispatched = await fixture.RunWorkerBatchAsync();

        // Stage 1 finds no matching Subscription and terminally classifies the Event as unrouted.
        dispatched.ShouldBe(0);
        fixture.DeliveryClient.Calls.ShouldBeEmpty();

        // No event_deliveries should have been created
        (await fixture.GetEventDeliveriesAsync(eventId)).ShouldBeEmpty();

        (await fixture.IsOutboxRowProcessedAsync(eventId)).ShouldBeTrue();
        (await fixture.GetEventStatusAsync(eventId)).ShouldBe("unrouted");
    }

    [Fact]
    public async Task Worker_EventWithoutTopic_MarksUnroutedAndCompletesOutbox()
    {
        var eventId = await fixture.InsertOrphanEventAndOutboxAsync("payment.created");

        var dispatched = await fixture.RunWorkerBatchAsync();

        dispatched.ShouldBe(0);
        (await fixture.GetEventDeliveriesAsync(eventId)).ShouldBeEmpty();
        (await fixture.GetEventStatusAsync(eventId)).ShouldBe("unrouted");
        (await fixture.IsOutboxRowProcessedAsync(eventId)).ShouldBeTrue();
    }

    [Fact]
    public async Task Worker_DeliveryFailure_SchedulesRetryOnEventDelivery()
    {
        fixture.DeliveryClient.ShouldSucceed = false;
        var eventId = await fixture.InsertEventAndOutboxAsync("payment.created");

        await fixture.RunWorkerBatchAsync();

        fixture.DeliveryClient.Calls.ShouldHaveSingleItem();

        // Outbox is processed after Stage 1 regardless of dispatch outcome
        (await fixture.IsOutboxRowProcessedAsync(eventId)).ShouldBeTrue();

        // Retry state lives on event_deliveries, scoped per-subscription
        var deliveries = await fixture.GetEventDeliveriesAsync(eventId);
        deliveries.ShouldHaveSingleItem();
        deliveries[0].Status.ShouldBe("pending");
        deliveries[0].AttemptCount.ShouldBe(1);
        deliveries[0].DeliverAfter.ShouldNotBeNull();
        (deliveries[0].DeliverAfter > DateTimeOffset.UtcNow).ShouldBeTrue();
    }

    [Fact]
    public async Task Worker_RetryAfterBackoff_DeliversOnSecondAttempt()
    {
        fixture.DeliveryClient.ShouldSucceed = false;
        var eventId = await fixture.InsertEventAndOutboxAsync("payment.created");

        // First attempt — fails, schedules retry on event_delivery
        await fixture.RunWorkerBatchAsync();
        fixture.DeliveryClient.Calls.ShouldHaveSingleItem();

        // Force the event_delivery's deliver_after to the past
        await fixture.ForceDeliveryRetryNowAsync(eventId);
        fixture.DeliveryClient.ShouldSucceed = true;

        // Second attempt — should succeed
        var dispatched = await fixture.RunWorkerBatchAsync();
        dispatched.ShouldBe(1);
        fixture.DeliveryClient.Calls.Count.ShouldBe(2);

        var deliveries = await fixture.GetEventDeliveriesAsync(eventId);
        deliveries.ShouldHaveSingleItem();
        deliveries[0].Status.ShouldBe("succeeded");
    }

    [Fact]
    public async Task Worker_RetryBeforeBackoffExpiry_DoesNotRedeliver()
    {
        fixture.DeliveryClient.ShouldSucceed = false;
        var eventId = await fixture.InsertEventAndOutboxAsync("payment.created");

        // First attempt — fails, schedules retry in the future
        await fixture.RunWorkerBatchAsync();
        fixture.DeliveryClient.Calls.ShouldHaveSingleItem();

        fixture.DeliveryClient.ShouldSucceed = true;

        // Second poll — event_delivery is not yet due, so no dispatch
        var dispatched = await fixture.RunWorkerBatchAsync();
        dispatched.ShouldBe(0);
        fixture.DeliveryClient.Calls.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Worker_ExhaustsRetries_DeadLettersDeliveryAndStopsRetrying()
    {
        fixture.DeliveryClient.ShouldSucceed = false;
        var eventId = await fixture.InsertEventAndOutboxAsync("payment.created");

        // Fail attempts up to MaxAttempts - 1, each time forcing deliver_after into the past
        for (var i = 1; i < RetryPolicy.DefaultMaxAttempts; i++)
        {
            await fixture.RunWorkerBatchAsync();
            await fixture.ForceDeliveryRetryNowAsync(eventId);
        }

        // Final attempt — should dead-letter the event_delivery
        await fixture.RunWorkerBatchAsync();

        var deliveries = await fixture.GetEventDeliveriesAsync(eventId);
        deliveries.ShouldHaveSingleItem();
        deliveries[0].Status.ShouldBe("dead_lettered");
    }

    [Fact]
    public async Task Worker_DeadLetteredDelivery_IsNotPickedUpAgain()
    {
        fixture.DeliveryClient.ShouldSucceed = false;
        var eventId = await fixture.InsertEventAndOutboxAsync("payment.created");

        for (var i = 1; i < RetryPolicy.DefaultMaxAttempts; i++)
        {
            await fixture.RunWorkerBatchAsync();
            await fixture.ForceDeliveryRetryNowAsync(eventId);
        }
        await fixture.RunWorkerBatchAsync(); // dead-letters

        fixture.DeliveryClient.Reset();
        fixture.DeliveryClient.ShouldSucceed = true;

        var dispatched = await fixture.RunWorkerBatchAsync();
        dispatched.ShouldBe(0);
        fixture.DeliveryClient.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Worker_TenantIsolation_OnlyRoutesWithinTenant()
    {
        var eventId = await fixture.InsertEventAndOutboxAsync("payment.created");
        var orphanEventId = await fixture.InsertOrphanEventAndOutboxAsync("payment.created");

        await fixture.RunWorkerBatchAsync();

        // Only one delivery — the orphan tenant has no topic
        fixture.DeliveryClient.Calls.ShouldHaveSingleItem();

        var deliveries = await fixture.GetEventDeliveriesAsync(eventId);
        deliveries.ShouldHaveSingleItem();
        deliveries[0].Status.ShouldBe("succeeded");

        // Orphan event got no event_deliveries (no topic match)
        (await fixture.GetEventDeliveriesAsync(orphanEventId)).ShouldBeEmpty();
        // Orphan outbox is processed (Stage 1 marks it processed even with no topic)
        (await fixture.IsOutboxRowProcessedAsync(orphanEventId)).ShouldBeTrue();
    }

    // The worker reads both the current { "event_type": "..." } shape and the
    // pre-v2.1 { "event_types": [...] } array shape during the migration compatibility window.
    // The fixture seeds subscriptions using the old array shape, so the tests above already
    // exercise the compat path. This test makes the intent explicit and documents the exit
    // condition: once all rows have been migrated to the new shape, remove the array branch
    // from SubscriptionRepository and delete this test.
    [Fact]
    public async Task Worker_LegacyEventTypesArrayShape_RoutesCorrectly()
    {
        // The seeded subscriptions intentionally use the pre-v2.1 event_types[] array shape.
        // Routing a known event type verifies the compat read path still works.
        var eventId = await fixture.InsertEventAndOutboxAsync("payment.created");

        await fixture.RunWorkerBatchAsync();

        var deliveries = await fixture.GetEventDeliveriesAsync(eventId);
        deliveries.ShouldNotBeEmpty();
        foreach (var d in deliveries)
            d.Status.ShouldBe("succeeded");
    }
}
