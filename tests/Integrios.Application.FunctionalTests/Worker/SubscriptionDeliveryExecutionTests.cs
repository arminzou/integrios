using System.Data.Common;
using Integrios.Application.Delivery;
using Integrios.Domain.Delivery;

namespace Integrios.Application.FunctionalTests.Worker;

public sealed class SubscriptionDeliveryExecutionTests : IClassFixture<WorkerRoutingFixture>, IAsyncLifetime
{
    private readonly WorkerRoutingFixture fixture;

    public SubscriptionDeliveryExecutionTests(WorkerRoutingFixture fixture)
    {
        this.fixture = fixture;
    }

    public async Task InitializeAsync() => await fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task ClaimNext_CreatesAttemptAndAdvancesDeliveryAtomically()
    {
        Guid deliveryId = await fixture.FanoutSingleDeliveryAsync();

        SubscriptionDeliveryWorkItem? claimed = await fixture.DeliveryQueue.ClaimNextAsync(CancellationToken.None);

        Assert.NotNull(claimed);
        Assert.Equal(deliveryId, claimed.Id);
        Assert.Equal(1, claimed.AttemptNumber);

        SubscriptionDeliveryState delivery = await fixture.GetSubscriptionDeliveryAsync(deliveryId);
        Assert.Equal("in_flight", delivery.Status);
        Assert.Equal(1, delivery.LifetimeAttemptCount);
        Assert.Equal(1, delivery.RetryCycleAttemptCount);
        Assert.Equal(claimed.AttemptId, delivery.ActiveAttemptId);
        Assert.NotNull(delivery.LeaseExpiresAt);

        DeliveryAttemptState attempt = Assert.Single(await fixture.GetDeliveryAttemptsAsync(deliveryId));
        Assert.Equal(claimed.AttemptId, attempt.Id);
        Assert.Equal(deliveryId, attempt.SubscriptionDeliveryId);
        Assert.Equal(1, attempt.AttemptNumber);
        Assert.Equal("in_progress", attempt.Status);
        Assert.Null(attempt.CompletedAt);
    }

    [Fact]
    public async Task ClaimNext_CompetingClaimsProduceOneOwnerAndOneAttempt()
    {
        Guid deliveryId = await fixture.FanoutSingleDeliveryAsync();

        SubscriptionDeliveryWorkItem?[] claims = await Task.WhenAll(
            fixture.DeliveryQueue.ClaimNextAsync(CancellationToken.None),
            fixture.DeliveryQueue.ClaimNextAsync(CancellationToken.None));

        SubscriptionDeliveryWorkItem claimed = Assert.Single(claims, claim => claim is not null)!;
        Assert.Equal(deliveryId, claimed.Id);
        Assert.Single(await fixture.GetDeliveryAttemptsAsync(deliveryId));

        SubscriptionDeliveryState delivery = await fixture.GetSubscriptionDeliveryAsync(deliveryId);
        Assert.Equal(claimed.AttemptId, delivery.ActiveAttemptId);
        Assert.Equal(1, delivery.LifetimeAttemptCount);
    }

    [Fact]
    public async Task ClaimNext_WhenDeliveryAdvanceFails_RollsBackAttemptAndClaim()
    {
        Guid deliveryId = await fixture.FanoutSingleDeliveryAsync();
        await fixture.WithDeliveryClaimFailureAsync(async () =>
            await Assert.ThrowsAnyAsync<DbException>(() => fixture.DeliveryQueue.ClaimNextAsync(CancellationToken.None)));

        await ConsistencyContractAssertions.ClaimFailureRollsBackAsync(
            deliveryId,
            fixture.GetSubscriptionDeliveryAsync,
            fixture.GetDeliveryAttemptsAsync);
    }

    [Fact]
    public async Task Finalize_WhenDeliveryAdvanceFails_RollsBackAttemptAndDelivery()
    {
        Guid deliveryId = await fixture.FanoutSingleDeliveryAsync();
        SubscriptionDeliveryWorkItem claimed = Assert.IsType<SubscriptionDeliveryWorkItem>(
            await fixture.DeliveryQueue.ClaimNextAsync(CancellationToken.None));
        await fixture.WithDeliveryFinalizationFailureAsync(async () =>
            await Assert.ThrowsAnyAsync<DbException>(() =>
                fixture.DeliveryQueue.FinalizeAsync(SuccessfulCompletion(claimed), CancellationToken.None)));

        await ConsistencyContractAssertions.FinalizationFailureRollsBackAsync(
            deliveryId,
            claimed.AttemptId,
            fixture.GetSubscriptionDeliveryAsync,
            fixture.GetDeliveryAttemptsAsync);
    }

    [Fact]
    public async Task ExpiredLease_ReclaimsWithNewFenceAndRejectsStaleFinalization()
    {
        Guid deliveryId = await fixture.FanoutSingleDeliveryAsync();
        SubscriptionDeliveryWorkItem? firstClaim = await fixture.DeliveryQueue.ClaimNextAsync(CancellationToken.None);
        Assert.NotNull(firstClaim);
        SubscriptionDeliveryWorkItem first = firstClaim;
        await fixture.ForceLeaseExpiredAsync(deliveryId);

        SubscriptionDeliveryWorkItem? secondClaim = await fixture.DeliveryQueue.ClaimNextAsync(CancellationToken.None);
        Assert.NotNull(secondClaim);
        SubscriptionDeliveryWorkItem second = secondClaim;

        Assert.NotEqual(first.AttemptId, second.AttemptId);
        Assert.Equal(2, second.AttemptNumber);
        IReadOnlyList<DeliveryAttemptState> attemptsAfterReclaim = await fixture.GetDeliveryAttemptsAsync(deliveryId);
        Assert.Equal(["indeterminate", "in_progress"], attemptsAfterReclaim.Select(attempt => attempt.Status));

        DeliveryFinalizationResult staleResult = await fixture.DeliveryQueue.FinalizeAsync(
            SuccessfulCompletion(first),
            CancellationToken.None);

        Assert.Equal(DeliveryFinalizationStatus.OwnershipLost, staleResult.Status);
        SubscriptionDeliveryState stillOwned = await fixture.GetSubscriptionDeliveryAsync(deliveryId);
        Assert.Equal("in_flight", stillOwned.Status);
        Assert.Equal(second.AttemptId, stillOwned.ActiveAttemptId);

        DeliveryFinalizationResult activeResult = await fixture.DeliveryQueue.FinalizeAsync(
            SuccessfulCompletion(second),
            CancellationToken.None);
        Assert.Equal(DeliveryFinalizationStatus.Applied, activeResult.Status);
        Assert.Equal(SubscriptionDeliveryDisposition.Succeeded, activeResult.Disposition);

        SubscriptionDeliveryState succeeded = await fixture.GetSubscriptionDeliveryAsync(deliveryId);
        Assert.Equal("succeeded", succeeded.Status);
        Assert.Null(succeeded.ActiveAttemptId);
        Assert.Null(succeeded.LeaseExpiresAt);
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
            SubscriptionDeliveryWorkItem? claim = await fixture.DeliveryQueue.ClaimNextAsync(CancellationToken.None);
            Assert.NotNull(claim);
            SubscriptionDeliveryWorkItem claimed = claim;
            Assert.Equal(expectedAttempt, claimed.AttemptNumber);
            await fixture.ForceLeaseExpiredAsync(deliveryId);
        }

        var recovery = Assert.IsType<RecoveredSubscriptionDeliveryDeadLetter>(
            await fixture.DeliveryQueue.ClaimNextWithRecoveryAsync(CancellationToken.None));
        Assert.Equal(deliveryId, recovery.DeliveryId);
        Assert.Equal(RetryPolicy.DefaultMaxAttempts, recovery.AttemptNumber);
        Assert.NotEqual(Guid.Empty, recovery.AttemptId);
        Assert.Null(await fixture.DeliveryQueue.ClaimNextAsync(CancellationToken.None));

        SubscriptionDeliveryState delivery = await fixture.GetSubscriptionDeliveryAsync(deliveryId);
        Assert.Equal("dead_lettered", delivery.Status);
        Assert.Equal(RetryPolicy.DefaultMaxAttempts, delivery.LifetimeAttemptCount);
        Assert.Equal(RetryPolicy.DefaultMaxAttempts, delivery.RetryCycleAttemptCount);
        Assert.Null(delivery.ActiveAttemptId);
        Assert.Null(delivery.LeaseExpiresAt);

        IReadOnlyList<DeliveryAttemptState> attempts = await fixture.GetDeliveryAttemptsAsync(deliveryId);
        Assert.Equal(RetryPolicy.DefaultMaxAttempts, attempts.Count);
        Assert.Equal([1, 2, 3], attempts.Select(attempt => attempt.AttemptNumber));
        Assert.All(attempts, attempt => Assert.Equal("indeterminate", attempt.Status));
    }

    [Fact]
    public async Task ExhaustedExpiredLease_DoesNotHideLaterPendingWork()
    {
        Guid exhaustedDeliveryId = await fixture.FanoutSingleDeliveryAsync();

        for (int attemptNumber = 1; attemptNumber <= RetryPolicy.DefaultMaxAttempts; attemptNumber++)
        {
            SubscriptionDeliveryWorkItem claimed = Assert.IsType<SubscriptionDeliveryWorkItem>(
                await fixture.DeliveryQueue.ClaimNextAsync(CancellationToken.None));
            Assert.Equal(exhaustedDeliveryId, claimed.Id);
            await fixture.ForceLeaseExpiredAsync(exhaustedDeliveryId);
        }

        Guid pendingDeliveryId = await fixture.FanoutSingleDeliveryAsync();

        SubscriptionDeliveryWorkItem next = Assert.IsType<SubscriptionDeliveryWorkItem>(
            await fixture.DeliveryQueue.ClaimNextAsync(CancellationToken.None));

        Assert.Equal(pendingDeliveryId, next.Id);
        Assert.Equal("dead_lettered", (await fixture.GetSubscriptionDeliveryAsync(exhaustedDeliveryId)).Status);
    }

    [Fact]
    public async Task Dispatch_WhenTransientFinalizationFails_RetriesDatabaseOnly()
    {
        Guid deliveryId = await fixture.FanoutSingleDeliveryAsync();
        await fixture.WithTransientFinalizationFailureAsync(async () =>
        {
            Assert.Equal(1, await fixture.RunDeliveryBatchAsync(1));
        });

        Assert.Single(fixture.DeliveryClient.Calls);
        Assert.Equal("succeeded", (await fixture.GetSubscriptionDeliveryAsync(deliveryId)).Status);
        Assert.Equal("succeeded", Assert.Single(await fixture.GetDeliveryAttemptsAsync(deliveryId)).Status);
    }

    [Fact]
    public async Task Replay_ResetsRetryCycleButAppendsLifetimeAttemptHistory()
    {
        Guid eventId = await fixture.InsertEventAndOutboxAsync("payment.created");
        Assert.Equal(1, await fixture.RunFanoutBatchAsync());
        SubscriptionDeliveryState initial = Assert.Single(await fixture.GetSubscriptionDeliveriesAsync(eventId));

        for (int expectedAttempt = 1; expectedAttempt <= RetryPolicy.DefaultMaxAttempts; expectedAttempt++)
        {
            SubscriptionDeliveryWorkItem? claim = await fixture.DeliveryQueue.ClaimNextAsync(CancellationToken.None);
            Assert.NotNull(claim);
            SubscriptionDeliveryWorkItem claimed = claim;
            Assert.Equal(expectedAttempt, claimed.AttemptNumber);
            DeliveryFinalizationResult result = await fixture.DeliveryQueue.FinalizeAsync(FailedHttpCompletion(claimed), CancellationToken.None);
            Assert.Equal(DeliveryFinalizationStatus.Applied, result.Status);

            if (expectedAttempt < RetryPolicy.DefaultMaxAttempts)
                await fixture.ForceDeliveryRetryNowAsync(eventId);
        }

        Assert.Equal(DeadLetterReplayResult.Replayed, await fixture.ReplayAsync(eventId, initial.Id));
        SubscriptionDeliveryState replayed = await fixture.GetSubscriptionDeliveryAsync(initial.Id);
        Assert.Equal(RetryPolicy.DefaultMaxAttempts, replayed.LifetimeAttemptCount);
        Assert.Equal(0, replayed.RetryCycleAttemptCount);

        SubscriptionDeliveryWorkItem? replayedClaim = await fixture.DeliveryQueue.ClaimNextAsync(CancellationToken.None);
        Assert.NotNull(replayedClaim);
        SubscriptionDeliveryWorkItem replayClaim = replayedClaim;
        Assert.Equal(4, replayClaim.AttemptNumber);

        IReadOnlyList<DeliveryAttemptState> attempts = await fixture.GetDeliveryAttemptsAsync(initial.Id);
        Assert.Equal([1, 2, 3, 4], attempts.Select(attempt => attempt.AttemptNumber));
        Assert.Equal("in_progress", attempts[^1].Status);

        SubscriptionDeliveryState activeReplay = await fixture.GetSubscriptionDeliveryAsync(initial.Id);
        Assert.Equal(4, activeReplay.LifetimeAttemptCount);
        Assert.Equal(1, activeReplay.RetryCycleAttemptCount);
    }

    private static DeliveryAttemptCompletion SuccessfulCompletion(SubscriptionDeliveryWorkItem claimed) => new(
        claimed.Id,
        claimed.AttemptId,
        true,
        null,
        claimed.PayloadJson,
        200,
        null,
        null);

    private static DeliveryAttemptCompletion FailedHttpCompletion(SubscriptionDeliveryWorkItem claimed) => new(
        claimed.Id,
        claimed.AttemptId,
        false,
        DeliveryFailurePhase.Http,
        claimed.PayloadJson,
        500,
        null,
        "HTTP 500");

    private static DeliveryAttemptCompletion TerminalHttpCompletion(SubscriptionDeliveryWorkItem claimed) => new(
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
        SubscriptionDeliveryWorkItem? claimed = await fixture.DeliveryQueue.ClaimNextAsync(CancellationToken.None);
        Assert.NotNull(claimed);

        DeliveryFinalizationResult result = await fixture.DeliveryQueue.FinalizeAsync(
            TerminalHttpCompletion(claimed), CancellationToken.None);

        Assert.Equal(DeliveryFinalizationStatus.Applied, result.Status);
        Assert.Equal(SubscriptionDeliveryDisposition.DeadLettered, result.Disposition);
        SubscriptionDeliveryState delivery = await fixture.GetSubscriptionDeliveryAsync(deliveryId);
        Assert.Equal("dead_lettered", delivery.Status);
        Assert.Equal(1, delivery.LifetimeAttemptCount);
    }

}
