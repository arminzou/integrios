using Integrios.Application.Delivery;

namespace Integrios.FunctionalTests.Worker;

public sealed class WorkerRetryAndDeadLetterTests : IClassFixture<WorkerRoutingFixture>, IAsyncLifetime
{
    private readonly WorkerRoutingFixture fixture;

    public WorkerRetryAndDeadLetterTests(WorkerRoutingFixture fixture)
    {
        this.fixture = fixture;
    }

    public async Task InitializeAsync() => await fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

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
}
