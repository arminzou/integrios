using Integrios.Application.Delivery;
using Integrios.Application.Outbox;

namespace Integrios.IntegrationTests;

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

        Assert.Equal(1, dispatched);
        Assert.Single(fixture.DeliveryClient.Calls);
        Assert.Equal(WorkerRoutingFixture.LedgerSinkUrl, fixture.DeliveryClient.Calls[0].Url);

        var deliveries = await fixture.GetSubscriptionDeliveriesAsync(eventId);
        Assert.Single(deliveries);
        Assert.Equal("succeeded", deliveries[0].Status);

        // Outbox row is always processed after Stage 1 fanout
        Assert.True(await fixture.IsOutboxRowProcessedAsync(eventId));
    }

    [Fact]
    public async Task Worker_SubscriptionMatchingSelectsByEventType_CorrectSinkReceivesDelivery()
    {
        var eventId = await fixture.InsertEventAndOutboxAsync("payment.authorized");

        await fixture.RunWorkerBatchAsync();

        Assert.Single(fixture.DeliveryClient.Calls);
        Assert.Equal(WorkerRoutingFixture.RiskSinkUrl, fixture.DeliveryClient.Calls[0].Url);

        var deliveries = await fixture.GetSubscriptionDeliveriesAsync(eventId);
        Assert.Single(deliveries);
        Assert.Equal("succeeded", deliveries[0].Status);
    }

    [Fact]
    public async Task Worker_NoMatchingTopic_SkipsGracefullyAndMarksOutboxProcessed()
    {
        var eventId = await fixture.InsertEventAndOutboxAsync("unknown.event.type");

        var dispatched = await fixture.RunWorkerBatchAsync();

        // Stage 1 ran and skipped the event (no topic), Stage 2 had nothing to dispatch
        Assert.Equal(0, dispatched);
        Assert.Empty(fixture.DeliveryClient.Calls);

        // No subscription_deliveries should have been created
        Assert.Empty(await fixture.GetSubscriptionDeliveriesAsync(eventId));

        Assert.True(await fixture.IsOutboxRowProcessedAsync(eventId));
    }

    [Fact]
    public async Task Worker_DeliveryFailure_SchedulesRetryOnSubscriptionDelivery()
    {
        fixture.DeliveryClient.ShouldSucceed = false;
        var eventId = await fixture.InsertEventAndOutboxAsync("payment.created");

        await fixture.RunWorkerBatchAsync();

        Assert.Single(fixture.DeliveryClient.Calls);

        // Outbox is processed after Stage 1 regardless of dispatch outcome
        Assert.True(await fixture.IsOutboxRowProcessedAsync(eventId));

        // Retry state lives on subscription_deliveries, scoped per-subscription
        var deliveries = await fixture.GetSubscriptionDeliveriesAsync(eventId);
        Assert.Single(deliveries);
        Assert.Equal("pending", deliveries[0].Status);
        Assert.Equal(1, deliveries[0].AttemptCount);
        Assert.NotNull(deliveries[0].DeliverAfter);
        Assert.True(deliveries[0].DeliverAfter > DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task Worker_RetryAfterBackoff_DeliversOnSecondAttempt()
    {
        fixture.DeliveryClient.ShouldSucceed = false;
        var eventId = await fixture.InsertEventAndOutboxAsync("payment.created");

        // First attempt — fails, schedules retry on subscription_delivery
        await fixture.RunWorkerBatchAsync();
        Assert.Single(fixture.DeliveryClient.Calls);

        // Force the subscription_delivery's deliver_after to the past
        await fixture.ForceDeliveryRetryNowAsync(eventId);
        fixture.DeliveryClient.ShouldSucceed = true;

        // Second attempt — should succeed
        var dispatched = await fixture.RunWorkerBatchAsync();
        Assert.Equal(1, dispatched);
        Assert.Equal(2, fixture.DeliveryClient.Calls.Count);

        var deliveries = await fixture.GetSubscriptionDeliveriesAsync(eventId);
        Assert.Single(deliveries);
        Assert.Equal("succeeded", deliveries[0].Status);
    }

    [Fact]
    public async Task Worker_RetryBeforeBackoffExpiry_DoesNotRedeliver()
    {
        fixture.DeliveryClient.ShouldSucceed = false;
        var eventId = await fixture.InsertEventAndOutboxAsync("payment.created");

        // First attempt — fails, schedules retry in the future
        await fixture.RunWorkerBatchAsync();
        Assert.Single(fixture.DeliveryClient.Calls);

        fixture.DeliveryClient.ShouldSucceed = true;

        // Second poll — subscription_delivery is not yet due, so no dispatch
        var dispatched = await fixture.RunWorkerBatchAsync();
        Assert.Equal(0, dispatched);
        Assert.Single(fixture.DeliveryClient.Calls);
    }

    [Fact]
    public async Task Worker_ExhaustsRetries_DeadLettersDeliveryAndStopsRetrying()
    {
        fixture.DeliveryClient.ShouldSucceed = false;
        var eventId = await fixture.InsertEventAndOutboxAsync("payment.created");

        // Fail attempts up to MaxAttempts - 1, each time forcing deliver_after into the past
        for (var i = 1; i < DispatchSubscriptionDeliveriesCommand.DefaultMaxAttempts; i++)
        {
            await fixture.RunWorkerBatchAsync();
            await fixture.ForceDeliveryRetryNowAsync(eventId);
        }

        // Final attempt — should dead-letter the subscription_delivery
        await fixture.RunWorkerBatchAsync();

        var deliveries = await fixture.GetSubscriptionDeliveriesAsync(eventId);
        Assert.Single(deliveries);
        Assert.Equal("dead_lettered", deliveries[0].Status);
    }

    [Fact]
    public async Task Worker_DeadLetteredDelivery_IsNotPickedUpAgain()
    {
        fixture.DeliveryClient.ShouldSucceed = false;
        var eventId = await fixture.InsertEventAndOutboxAsync("payment.created");

        for (var i = 1; i < DispatchSubscriptionDeliveriesCommand.DefaultMaxAttempts; i++)
        {
            await fixture.RunWorkerBatchAsync();
            await fixture.ForceDeliveryRetryNowAsync(eventId);
        }
        await fixture.RunWorkerBatchAsync(); // dead-letters

        fixture.DeliveryClient.Reset();
        fixture.DeliveryClient.ShouldSucceed = true;

        var dispatched = await fixture.RunWorkerBatchAsync();
        Assert.Equal(0, dispatched);
        Assert.Empty(fixture.DeliveryClient.Calls);
    }

    [Fact]
    public async Task Worker_TenantIsolation_OnlyRoutesWithinTenant()
    {
        var eventId = await fixture.InsertEventAndOutboxAsync("payment.created");
        var orphanEventId = await fixture.InsertOrphanEventAndOutboxAsync("payment.created");

        await fixture.RunWorkerBatchAsync();

        // Only one delivery — the orphan tenant has no topic
        Assert.Single(fixture.DeliveryClient.Calls);

        var deliveries = await fixture.GetSubscriptionDeliveriesAsync(eventId);
        Assert.Single(deliveries);
        Assert.Equal("succeeded", deliveries[0].Status);

        // Orphan event got no subscription_deliveries (no topic match)
        Assert.Empty(await fixture.GetSubscriptionDeliveriesAsync(orphanEventId));
        // Orphan outbox is processed (Stage 1 marks it processed even with no topic)
        Assert.True(await fixture.IsOutboxRowProcessedAsync(orphanEventId));
    }

    // ADR-0015: the worker reads both the current { "event_type": "..." } shape and the
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

        var deliveries = await fixture.GetSubscriptionDeliveriesAsync(eventId);
        Assert.NotEmpty(deliveries);
        Assert.All(deliveries, d => Assert.Equal("succeeded", d.Status));
    }
}
