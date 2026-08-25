using System.Data.Common;
using Integrios.Application.Delivery;
using Integrios.Domain.Entities;
using Integrios.Domain.Enums;

namespace Integrios.FunctionalTests.Worker;

public sealed class EventDeliveryExecutionTests : IClassFixture<WorkerRoutingFixture>, IAsyncLifetime
{
    private readonly WorkerRoutingFixture fixture;

    public EventDeliveryExecutionTests(WorkerRoutingFixture fixture)
    {
        this.fixture = fixture;
    }

    public async Task InitializeAsync() => await fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task ClaimNext_CreatesAttemptAndAdvancesDeliveryAtomically()
    {
        Guid deliveryId = await fixture.FanoutSingleDeliveryAsync();

        EventDeliveryWorkItem? claimed = await fixture.DeliveryQueue.ClaimNextAsync(CancellationToken.None);

        claimed.ShouldNotBeNull();
        claimed.Id.ShouldBe(deliveryId);
        claimed.AttemptNumber.ShouldBe(1);

        EventDeliveryState delivery = await fixture.GetEventDeliveryAsync(deliveryId);
        delivery.Status.ShouldBe("in_flight");
        delivery.LifetimeAttemptCount.ShouldBe(1);
        delivery.RetryCycleAttemptCount.ShouldBe(1);
        delivery.ActiveAttemptId.ShouldBe(claimed.AttemptId);
        delivery.LeaseExpiresAt.ShouldNotBeNull();

        DeliveryAttemptState attempt = (await fixture.GetDeliveryAttemptsAsync(deliveryId)).ShouldHaveSingleItem();
        attempt.Id.ShouldBe(claimed.AttemptId);
        attempt.EventDeliveryId.ShouldBe(deliveryId);
        attempt.AttemptNumber.ShouldBe(1);
        attempt.Status.ShouldBe("in_progress");
        attempt.CompletedAt.ShouldBeNull();
    }

    [Fact]
    public async Task ClaimNext_CompetingClaimsProduceOneOwnerAndOneAttempt()
    {
        Guid deliveryId = await fixture.FanoutSingleDeliveryAsync();

        EventDeliveryWorkItem?[] claims = await Task.WhenAll(
            fixture.DeliveryQueue.ClaimNextAsync(CancellationToken.None),
            fixture.DeliveryQueue.ClaimNextAsync(CancellationToken.None));

        EventDeliveryWorkItem claimed = claims.Where(claim => claim is not null).ShouldHaveSingleItem()!;
        claimed.Id.ShouldBe(deliveryId);
        (await fixture.GetDeliveryAttemptsAsync(deliveryId)).ShouldHaveSingleItem();

        EventDeliveryState delivery = await fixture.GetEventDeliveryAsync(deliveryId);
        delivery.ActiveAttemptId.ShouldBe(claimed.AttemptId);
        delivery.LifetimeAttemptCount.ShouldBe(1);
    }

    [Fact]
    public async Task ClaimNext_WhenDeliveryAdvanceFails_RollsBackAttemptAndClaim()
    {
        Guid deliveryId = await fixture.FanoutSingleDeliveryAsync();
        await fixture.WithDeliveryClaimFailureAsync(async () =>
            await Should.ThrowAsync<DbException>(() => fixture.DeliveryQueue.ClaimNextAsync(CancellationToken.None)));

        await ConsistencyContractAssertions.ClaimFailureRollsBackAsync(
            deliveryId,
            fixture.GetEventDeliveryAsync,
            fixture.GetDeliveryAttemptsAsync);
    }

    [Fact]
    public async Task Finalize_WhenDeliveryAdvanceFails_RollsBackAttemptAndDelivery()
    {
        Guid deliveryId = await fixture.FanoutSingleDeliveryAsync();
        EventDeliveryWorkItem claimed = (await fixture.DeliveryQueue.ClaimNextAsync(CancellationToken.None))
            .ShouldBeOfType<EventDeliveryWorkItem>();
        await fixture.WithDeliveryFinalizationFailureAsync(async () =>
            await Should.ThrowAsync<DbException>(() =>
                fixture.DeliveryQueue.FinalizeAsync(SuccessfulCompletion(claimed), CancellationToken.None)));

        await ConsistencyContractAssertions.FinalizationFailureRollsBackAsync(
            deliveryId,
            claimed.AttemptId,
            fixture.GetEventDeliveryAsync,
            fixture.GetDeliveryAttemptsAsync);
    }

    [Fact]
    public async Task ExpiredLease_ReclaimsWithNewFenceAndRejectsStaleFinalization()
    {
        Guid deliveryId = await fixture.FanoutSingleDeliveryAsync();
        EventDeliveryWorkItem? firstClaim = await fixture.DeliveryQueue.ClaimNextAsync(CancellationToken.None);
        firstClaim.ShouldNotBeNull();
        EventDeliveryWorkItem first = firstClaim;
        await fixture.ForceLeaseExpiredAsync(deliveryId);

        EventDeliveryWorkItem? secondClaim = await fixture.DeliveryQueue.ClaimNextAsync(CancellationToken.None);
        secondClaim.ShouldNotBeNull();
        EventDeliveryWorkItem second = secondClaim;

        second.AttemptId.ShouldNotBe(first.AttemptId);
        second.AttemptNumber.ShouldBe(2);
        IReadOnlyList<DeliveryAttemptState> attemptsAfterReclaim = await fixture.GetDeliveryAttemptsAsync(deliveryId);
        attemptsAfterReclaim.Select(attempt => attempt.Status).ShouldBe(["indeterminate", "in_progress"]);

        DeliveryFinalizationResult staleResult = await fixture.DeliveryQueue.FinalizeAsync(
            SuccessfulCompletion(first),
            CancellationToken.None);

        staleResult.Status.ShouldBe(DeliveryFinalizationStatus.OwnershipLost);
        EventDeliveryState stillOwned = await fixture.GetEventDeliveryAsync(deliveryId);
        stillOwned.Status.ShouldBe("in_flight");
        stillOwned.ActiveAttemptId.ShouldBe(second.AttemptId);

        DeliveryFinalizationResult activeResult = await fixture.DeliveryQueue.FinalizeAsync(
            SuccessfulCompletion(second),
            CancellationToken.None);
        activeResult.Status.ShouldBe(DeliveryFinalizationStatus.Applied);
        activeResult.Disposition.ShouldBe(EventDeliveryDisposition.Succeeded);

        EventDeliveryState succeeded = await fixture.GetEventDeliveryAsync(deliveryId);
        succeeded.Status.ShouldBe("succeeded");
        succeeded.ActiveAttemptId.ShouldBeNull();
        succeeded.LeaseExpiresAt.ShouldBeNull();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExpiredLease_ReclaimAndFinalizationRace_HonorsTheRowLockWinner(bool finalizationWins)
    {
        await fixture.RunExpiredLeaseRaceAsync(finalizationWins);
    }

    [Fact]
    public async Task RepeatedExpiredLeases_ConsumeBudgetAndDeadLetterWithoutExtraAttempt()
    {
        Guid deliveryId = await fixture.FanoutSingleDeliveryAsync();

        for (int expectedAttempt = 1; expectedAttempt <= RetryPolicy.DefaultMaxAttempts; expectedAttempt++)
        {
            EventDeliveryWorkItem? claim = await fixture.DeliveryQueue.ClaimNextAsync(CancellationToken.None);
            claim.ShouldNotBeNull();
            EventDeliveryWorkItem claimed = claim;
            claimed.AttemptNumber.ShouldBe(expectedAttempt);
            await fixture.ForceLeaseExpiredAsync(deliveryId);
        }

        var recovery = (await fixture.DeliveryQueue.ClaimNextWithRecoveryAsync(CancellationToken.None))
            .ShouldBeOfType<RecoveredEventDeliveryDeadLetter>();
        recovery.DeliveryId.ShouldBe(deliveryId);
        recovery.AttemptNumber.ShouldBe(RetryPolicy.DefaultMaxAttempts);
        recovery.AttemptId.ShouldNotBe(Guid.Empty);
        (await fixture.DeliveryQueue.ClaimNextAsync(CancellationToken.None)).ShouldBeNull();

        EventDeliveryState delivery = await fixture.GetEventDeliveryAsync(deliveryId);
        delivery.Status.ShouldBe("dead_lettered");
        delivery.LifetimeAttemptCount.ShouldBe(RetryPolicy.DefaultMaxAttempts);
        delivery.RetryCycleAttemptCount.ShouldBe(RetryPolicy.DefaultMaxAttempts);
        delivery.ActiveAttemptId.ShouldBeNull();
        delivery.LeaseExpiresAt.ShouldBeNull();

        IReadOnlyList<DeliveryAttemptState> attempts = await fixture.GetDeliveryAttemptsAsync(deliveryId);
        attempts.Count.ShouldBe(RetryPolicy.DefaultMaxAttempts);
        attempts.Select(attempt => attempt.AttemptNumber).ShouldBe([1, 2, 3]);
        foreach (var attempt in attempts)
            attempt.Status.ShouldBe("indeterminate");
    }

    [Fact]
    public async Task ExhaustedExpiredLease_DoesNotHideLaterPendingWork()
    {
        Guid exhaustedDeliveryId = await fixture.FanoutSingleDeliveryAsync();

        for (int attemptNumber = 1; attemptNumber <= RetryPolicy.DefaultMaxAttempts; attemptNumber++)
        {
            EventDeliveryWorkItem claimed = (await fixture.DeliveryQueue.ClaimNextAsync(CancellationToken.None))
                .ShouldBeOfType<EventDeliveryWorkItem>();
            claimed.Id.ShouldBe(exhaustedDeliveryId);
            await fixture.ForceLeaseExpiredAsync(exhaustedDeliveryId);
        }

        Guid pendingDeliveryId = await fixture.FanoutSingleDeliveryAsync();

        EventDeliveryWorkItem next = (await fixture.DeliveryQueue.ClaimNextAsync(CancellationToken.None))
            .ShouldBeOfType<EventDeliveryWorkItem>();

        next.Id.ShouldBe(pendingDeliveryId);
        (await fixture.GetEventDeliveryAsync(exhaustedDeliveryId)).Status.ShouldBe("dead_lettered");
    }

    [Fact]
    public async Task Dispatch_WhenTransientFinalizationFails_RetriesDatabaseOnly()
    {
        Guid deliveryId = await fixture.FanoutSingleDeliveryAsync();
        await fixture.WithTransientFinalizationFailureAsync(async () =>
        {
            (await fixture.RunDeliveryBatchAsync(1)).ShouldBe(1);
        });

        fixture.DeliveryClient.Calls.ShouldHaveSingleItem();
        (await fixture.GetEventDeliveryAsync(deliveryId)).Status.ShouldBe("succeeded");
        (await fixture.GetDeliveryAttemptsAsync(deliveryId)).ShouldHaveSingleItem().Status.ShouldBe("succeeded");
    }

    [Fact]
    public async Task Replay_ResetsRetryCycleButAppendsLifetimeAttemptHistory()
    {
        Guid eventId = await fixture.InsertEventAndOutboxAsync("payment.created");
        (await fixture.RunFanoutBatchAsync()).ShouldBe(1);
        EventDeliveryState initial = (await fixture.GetEventDeliveriesAsync(eventId)).ShouldHaveSingleItem();

        for (int expectedAttempt = 1; expectedAttempt <= RetryPolicy.DefaultMaxAttempts; expectedAttempt++)
        {
            EventDeliveryWorkItem? claim = await fixture.DeliveryQueue.ClaimNextAsync(CancellationToken.None);
            claim.ShouldNotBeNull();
            EventDeliveryWorkItem claimed = claim;
            claimed.AttemptNumber.ShouldBe(expectedAttempt);
            DeliveryFinalizationResult result = await fixture.DeliveryQueue.FinalizeAsync(FailedHttpCompletion(claimed), CancellationToken.None);
            result.Status.ShouldBe(DeliveryFinalizationStatus.Applied);

            if (expectedAttempt < RetryPolicy.DefaultMaxAttempts)
                await fixture.ForceDeliveryRetryNowAsync(eventId);
        }

        (await fixture.ReplayAsync(eventId, initial.Id)).ShouldBe(DeadLetterReplayResult.Replayed);
        EventDeliveryState replayed = await fixture.GetEventDeliveryAsync(initial.Id);
        replayed.LifetimeAttemptCount.ShouldBe(RetryPolicy.DefaultMaxAttempts);
        replayed.RetryCycleAttemptCount.ShouldBe(0);

        EventDeliveryWorkItem? replayedClaim = await fixture.DeliveryQueue.ClaimNextAsync(CancellationToken.None);
        replayedClaim.ShouldNotBeNull();
        EventDeliveryWorkItem replayClaim = replayedClaim;
        replayClaim.AttemptNumber.ShouldBe(4);

        IReadOnlyList<DeliveryAttemptState> attempts = await fixture.GetDeliveryAttemptsAsync(initial.Id);
        attempts.Select(attempt => attempt.AttemptNumber).ShouldBe([1, 2, 3, 4]);
        attempts[^1].Status.ShouldBe("in_progress");

        EventDeliveryState activeReplay = await fixture.GetEventDeliveryAsync(initial.Id);
        activeReplay.LifetimeAttemptCount.ShouldBe(4);
        activeReplay.RetryCycleAttemptCount.ShouldBe(1);
    }

    private static DeliveryAttemptCompletion SuccessfulCompletion(EventDeliveryWorkItem claimed) => new(
        claimed.Id,
        claimed.AttemptId,
        true,
        null,
        claimed.PayloadJson,
        200,
        null,
        null);

    private static DeliveryAttemptCompletion FailedHttpCompletion(EventDeliveryWorkItem claimed) => new(
        claimed.Id,
        claimed.AttemptId,
        false,
        DeliveryFailurePhase.Http,
        claimed.PayloadJson,
        500,
        null,
        "HTTP 500");

    private static DeliveryAttemptCompletion TerminalHttpCompletion(EventDeliveryWorkItem claimed) => new(
        claimed.Id,
        claimed.AttemptId,
        false,
        DeliveryFailurePhase.Http,
        claimed.PayloadJson,
        404,
        null,
        "HTTP 404",
        IsTerminalFailure: true);

    [Fact]
    public async Task Finalize_TerminalHttpFailure_DeadLettersImmediatelyOnFirstAttempt()
    {
        Guid deliveryId = await fixture.FanoutSingleDeliveryAsync();
        EventDeliveryWorkItem? claimed = await fixture.DeliveryQueue.ClaimNextAsync(CancellationToken.None);
        claimed.ShouldNotBeNull();

        DeliveryFinalizationResult result = await fixture.DeliveryQueue.FinalizeAsync(
            TerminalHttpCompletion(claimed), CancellationToken.None);

        result.Status.ShouldBe(DeliveryFinalizationStatus.Applied);
        result.Disposition.ShouldBe(EventDeliveryDisposition.DeadLettered);
        EventDeliveryState delivery = await fixture.GetEventDeliveryAsync(deliveryId);
        delivery.Status.ShouldBe("dead_lettered");
        delivery.LifetimeAttemptCount.ShouldBe(1);
    }

}
