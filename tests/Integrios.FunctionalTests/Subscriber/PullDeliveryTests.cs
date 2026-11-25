using Integrios.Application.Delivery;
using Integrios.Application.PullDelivery;
using Integrios.Application.Telemetry;
using Integrios.FunctionalTests.Worker;
using Integrios.Tests.Shared;

namespace Integrios.FunctionalTests.Subscriber;

public sealed class PullDeliveryTests : IClassFixture<WorkerRoutingFixture>, IAsyncLifetime
{
    private static readonly Guid A = WorkerRoutingFixture.PullDestinationAId;
    private static readonly Guid B = WorkerRoutingFixture.PullDestinationBId;
    private readonly WorkerRoutingFixture fixture;

    public PullDeliveryTests(WorkerRoutingFixture fixture)
    {
        this.fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        await fixture.ResetAsync();
        await fixture.SeedPullDestinationsAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private Task<IReadOnlyList<PullDelivery>> ClaimAsync(Guid destinationId, int max = 32) =>
        fixture.ClaimPullAsync(destinationId, max);

    private static PullAttemptOwner Owner(Guid destinationId, PullDelivery delivery) =>
        new(destinationId, delivery.DeliveryId, delivery.AttemptId);

    [Fact]
    public async Task Fanout_WritesPullRowWithoutHttpSnapshot()
    {
        Guid deliveryId = await fixture.FanoutPullDeliveryAsync("order.placed");

        DeliveryTransportState transport = await fixture.GetDeliveryTransportAsync(deliveryId);
        transport.Transport.ShouldBe("pull");
        transport.HasSnapshot.ShouldBe(0);
    }

    [Fact]
    public async Task Claim_ReturnsMappedPayloadAndSpendsOneAttempt()
    {
        Guid deliveryId = await fixture.FanoutPullDeliveryAsync("order.placed");

        PullDelivery claimed = (await ClaimAsync(A)).ShouldHaveSingleItem();

        claimed.DeliveryId.ShouldBe(deliveryId);
        claimed.AttemptNumber.ShouldBe(1);
        claimed.EventType.ShouldBe("order.placed");
        claimed.PayloadJson.ShouldBe("""{"mapped":true}""");
        EventDeliveryState delivery = await fixture.GetEventDeliveryAsync(deliveryId);
        delivery.Status.ShouldBe("in_flight");
        delivery.ActiveAttemptId.ShouldBe(claimed.AttemptId);
        delivery.LeaseExpiresAt.ShouldBe(claimed.LeaseExpiresAt);
        (await ClaimAsync(A)).ShouldBeEmpty();
    }

    [Fact]
    public async Task EmptyClaim_SpendsNothing()
    {
        (await ClaimAsync(A)).ShouldBeEmpty();
        (await ClaimAsync(B)).ShouldBeEmpty();
    }

    [Fact]
    public async Task PushWorker_NeverTakesPullDeliveries()
    {
        Guid deliveryId = await fixture.FanoutPullDeliveryAsync("order.placed");

        (await fixture.RunDeliveryBatchAsync()).ShouldBe(0);

        fixture.DeliveryClient.Calls.ShouldBeEmpty();
        (await fixture.GetEventDeliveryAsync(deliveryId)).LifetimeAttemptCount.ShouldBe(0);
    }

    [Fact]
    public async Task PullClaim_NeverTakesPushDeliveries()
    {
        Guid pushDeliveryId = await fixture.FanoutSingleDeliveryAsync("payment.created");

        (await ClaimAsync(A)).ShouldBeEmpty();
        (await ClaimAsync(B)).ShouldBeEmpty();
        (await fixture.GetEventDeliveryAsync(pushDeliveryId)).LifetimeAttemptCount.ShouldBe(0);
    }

    [Fact]
    public async Task PullClaim_NeverTakesAPushDeliveryEvenOnItsOwnDestination()
    {
        Guid pushDeliveryId = await fixture.FanoutSingleDeliveryAsync("payment.created");
        await fixture.MoveDeliveryToDestinationAsync(pushDeliveryId, A);

        (await ClaimAsync(A)).ShouldBeEmpty();
        (await fixture.GetEventDeliveryAsync(pushDeliveryId)).LifetimeAttemptCount.ShouldBe(0);
    }

    [Fact]
    public async Task Claim_IsScopedToItsDestination()
    {
        Guid forB = await fixture.FanoutPullDeliveryAsync("order.shipped");

        (await ClaimAsync(A)).ShouldBeEmpty();
        (await fixture.GetEventDeliveryAsync(forB)).LifetimeAttemptCount.ShouldBe(0);
        (await ClaimAsync(B)).ShouldHaveSingleItem().DeliveryId.ShouldBe(forB);
    }

    [Fact]
    public async Task AnotherDestination_CannotRenewOrSettle()
    {
        await fixture.FanoutPullDeliveryAsync("order.shipped");
        PullDelivery claimed = (await ClaimAsync(B)).ShouldHaveSingleItem();
        PullAttemptOwner asA = Owner(A, claimed);

        (await fixture.PullQueue.RenewAsync(asA, CancellationToken.None)).Status
            .ShouldBe(PullLeaseRenewalStatus.OwnershipLost);
        (await fixture.PullQueue.SettleAsync(asA, PullSettlement.Ack(), CancellationToken.None)).Status
            .ShouldBe(PullSettlementStatus.OwnershipLost);
        (await fixture.GetEventDeliveryAsync(claimed.DeliveryId)).Status.ShouldBe("in_flight");
    }

    [Fact]
    public async Task Renew_ExtendsTheLease()
    {
        await fixture.FanoutPullDeliveryAsync("order.placed");
        PullDelivery claimed = (await ClaimAsync(A)).ShouldHaveSingleItem();
        await Task.Delay(50);

        PullLeaseRenewalResult renewed = await fixture.PullQueue.RenewAsync(Owner(A, claimed), CancellationToken.None);

        renewed.Status.ShouldBe(PullLeaseRenewalStatus.Renewed);
        renewed.LeaseExpiresAt.ShouldNotBeNull();
        renewed.LeaseExpiresAt.Value.ShouldBeGreaterThan(claimed.LeaseExpiresAt);
        (await fixture.GetEventDeliveryAsync(claimed.DeliveryId)).LeaseExpiresAt.ShouldBe(renewed.LeaseExpiresAt);
    }

    [Fact]
    public async Task Renew_IsCappedAtTheAttemptCeilingAndRefusedPastIt()
    {
        await fixture.FanoutPullDeliveryAsync("order.placed");
        PullDelivery claimed = (await ClaimAsync(A)).ShouldHaveSingleItem();

        await fixture.BackdateActiveAttemptAsync(claimed.DeliveryId, TimeSpan.FromMinutes(29));
        PullLeaseRenewalResult capped = await fixture.PullQueue.RenewAsync(Owner(A, claimed), CancellationToken.None);
        capped.Status.ShouldBe(PullLeaseRenewalStatus.Renewed);
        capped.LeaseExpiresAt!.Value.ShouldBeLessThan(DateTimeOffset.UtcNow.AddSeconds(61));

        await fixture.BackdateActiveAttemptAsync(claimed.DeliveryId, TimeSpan.FromMinutes(2));
        (await fixture.PullQueue.RenewAsync(Owner(A, claimed), CancellationToken.None)).Status
            .ShouldBe(PullLeaseRenewalStatus.MaxAttemptDurationReached);
    }

    [Fact]
    public async Task Ack_SucceedsAndARepeatedAckConverges()
    {
        await fixture.FanoutPullDeliveryAsync("order.placed");
        PullDelivery claimed = (await ClaimAsync(A)).ShouldHaveSingleItem();

        (await fixture.PullQueue.SettleAsync(Owner(A, claimed), PullSettlement.Ack(), CancellationToken.None)).Status
            .ShouldBe(PullSettlementStatus.Applied);
        (await fixture.PullQueue.SettleAsync(Owner(A, claimed), PullSettlement.Ack(), CancellationToken.None)).Status
            .ShouldBe(PullSettlementStatus.AlreadyApplied);
        (await fixture.PullQueue.SettleAsync(Owner(A, claimed), PullSettlement.Release(null, null), CancellationToken.None)).Status
            .ShouldBe(PullSettlementStatus.OwnershipLost);

        (await fixture.GetEventDeliveryAsync(claimed.DeliveryId)).Status.ShouldBe("succeeded");
        DeliveryAttemptState attempt = (await fixture.GetDeliveryAttemptsAsync(claimed.DeliveryId)).ShouldHaveSingleItem();
        attempt.Status.ShouldBe("succeeded");
        attempt.FailurePhase.ShouldBeNull();
    }

    [Fact]
    public async Task Release_SchedulesARetryRecordedAsASubscriberFailure()
    {
        await fixture.FanoutPullDeliveryAsync("order.placed");
        PullDelivery claimed = (await ClaimAsync(A)).ShouldHaveSingleItem();

        (await fixture.PullQueue.SettleAsync(
                Owner(A, claimed),
                PullSettlement.Release("provider unavailable", TimeSpan.FromMinutes(5)),
                CancellationToken.None)).Status
            .ShouldBe(PullSettlementStatus.Applied);

        EventDeliveryState delivery = await fixture.GetEventDeliveryAsync(claimed.DeliveryId);
        delivery.Status.ShouldBe("pending");
        delivery.DeliverAfter.ShouldNotBeNull();
        delivery.DeliverAfter.Value.ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddMinutes(4));
        DeliveryAttemptState attempt = (await fixture.GetDeliveryAttemptsAsync(claimed.DeliveryId)).ShouldHaveSingleItem();
        attempt.Status.ShouldBe("failed");
        attempt.FailurePhase.ShouldBe("subscriber");
        attempt.ErrorMessage.ShouldBe("provider unavailable");
        (await ClaimAsync(A)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Fail_DeadLetters()
    {
        await fixture.FanoutPullDeliveryAsync("order.placed");
        PullDelivery claimed = (await ClaimAsync(A)).ShouldHaveSingleItem();

        (await fixture.PullQueue.SettleAsync(Owner(A, claimed), PullSettlement.Reject("order rejected"), CancellationToken.None)).Status
            .ShouldBe(PullSettlementStatus.Applied);

        (await fixture.GetEventDeliveryAsync(claimed.DeliveryId)).Status.ShouldBe("dead_lettered");
        DeliveryAttemptState attempt = (await fixture.GetDeliveryAttemptsAsync(claimed.DeliveryId)).ShouldHaveSingleItem();
        attempt.FailurePhase.ShouldBe("subscriber");
        attempt.ErrorMessage.ShouldBe("order rejected");
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task RepeatedFailure_OnlyAcceptsTheOriginalSettlement(bool terminal, bool exhaustRetries)
    {
        await fixture.FanoutPullDeliveryAsync("order.placed");
        if (exhaustRetries)
        {
            for (int attempt = 1; attempt < RetryPolicy.DefaultMaxAttempts; attempt++)
            {
                PullDelivery previous = (await ClaimAsync(A)).ShouldHaveSingleItem();
                await fixture.ForceLeaseExpiredAsync(previous.DeliveryId);
            }
        }

        PullDelivery claimed = (await ClaimAsync(A)).ShouldHaveSingleItem();
        PullAttemptOwner owner = Owner(A, claimed);
        PullSettlement original = terminal ? PullSettlement.Reject("rejected") : PullSettlement.Release("retry", null);
        PullSettlement different = terminal ? PullSettlement.Release(null, null) : PullSettlement.Reject("rejected");

        PullSettlementResult applied = await fixture.PullQueue.SettleAsync(owner, original, CancellationToken.None);
        applied.Status.ShouldBe(PullSettlementStatus.Applied);
        applied.Disposition.ShouldBe(terminal || exhaustRetries
            ? EventDeliveryDisposition.DeadLettered
            : EventDeliveryDisposition.RetryScheduled);
        (await fixture.PullQueue.SettleAsync(owner, original, CancellationToken.None)).Status
            .ShouldBe(PullSettlementStatus.AlreadyApplied);
        (await fixture.PullQueue.SettleAsync(owner, different, CancellationToken.None)).Status
            .ShouldBe(PullSettlementStatus.OwnershipLost);
        (await fixture.PullQueue.SettleAsync(owner, PullSettlement.Ack(), CancellationToken.None)).Status
            .ShouldBe(PullSettlementStatus.OwnershipLost);

        (await fixture.GetEventDeliveryAsync(claimed.DeliveryId)).Status
            .ShouldBe(terminal || exhaustRetries ? "dead_lettered" : "pending");

        if (terminal || exhaustRetries)
        {
            await fixture.ReplayAsync(claimed.EventId, claimed.DeliveryId);
            (await ClaimAsync(A)).ShouldHaveSingleItem();
            (await fixture.PullQueue.SettleAsync(owner, original, CancellationToken.None)).Status
                .ShouldBe(PullSettlementStatus.AlreadyApplied);
            (await fixture.PullQueue.SettleAsync(owner, different, CancellationToken.None)).Status
                .ShouldBe(PullSettlementStatus.OwnershipLost);
            (await fixture.GetEventDeliveryAsync(claimed.DeliveryId)).Status.ShouldBe("in_flight");
        }
    }

    [Fact]
    public async Task ExpiredLease_StillSettlesUntilReclaimed()
    {
        await fixture.FanoutPullDeliveryAsync("order.placed");
        PullDelivery claimed = (await ClaimAsync(A)).ShouldHaveSingleItem();
        await fixture.ForceLeaseExpiredAsync(claimed.DeliveryId);

        (await fixture.PullQueue.SettleAsync(Owner(A, claimed), PullSettlement.Ack(), CancellationToken.None)).Status
            .ShouldBe(PullSettlementStatus.Applied);
        (await fixture.GetEventDeliveryAsync(claimed.DeliveryId)).Status.ShouldBe("succeeded");
    }

    [Fact]
    public async Task ExpiredLease_IsRecoveredByTheNextClaimAndFencesTheOldAttempt()
    {
        await fixture.FanoutPullDeliveryAsync("order.placed");
        PullDelivery first = (await ClaimAsync(A)).ShouldHaveSingleItem();
        await fixture.ForceLeaseExpiredAsync(first.DeliveryId);

        PullDelivery second = (await ClaimAsync(A)).ShouldHaveSingleItem();

        second.DeliveryId.ShouldBe(first.DeliveryId);
        second.AttemptId.ShouldNotBe(first.AttemptId);
        second.AttemptNumber.ShouldBe(2);
        IReadOnlyList<DeliveryAttemptState> attempts = await fixture.GetDeliveryAttemptsAsync(first.DeliveryId);
        attempts.Select(attempt => attempt.Status).ShouldBe(["indeterminate", "in_progress"]);

        (await fixture.PullQueue.RenewAsync(Owner(A, first), CancellationToken.None)).Status
            .ShouldBe(PullLeaseRenewalStatus.OwnershipLost);
        (await fixture.PullQueue.SettleAsync(Owner(A, first), PullSettlement.Ack(), CancellationToken.None)).Status
            .ShouldBe(PullSettlementStatus.OwnershipLost);
        (await fixture.PullQueue.SettleAsync(Owner(A, second), PullSettlement.Ack(), CancellationToken.None)).Status
            .ShouldBe(PullSettlementStatus.Applied);
    }

    [Fact]
    public async Task RecoveredDeadLetter_IsLoggedAndCountedLikePush()
    {
        using var metrics = new MetricCollector(IntegriosMetrics.MeterName);
        Guid deliveryId = await fixture.FanoutPullDeliveryAsync("order.placed");
        for (int attempt = 1; attempt <= RetryPolicy.DefaultMaxAttempts; attempt++)
        {
            (await ClaimAsync(A)).ShouldHaveSingleItem();
            await fixture.ForceLeaseExpiredAsync(deliveryId);
        }

        (await ClaimAsync(A)).ShouldBeEmpty();

        (await fixture.GetEventDeliveryAsync(deliveryId)).Status.ShouldBe("dead_lettered");
        metrics.ForInstrument("integrios_deliveries_dead_lettered")
            .ShouldContain(measurement => Equals(measurement.Tag("connector_key"), "pull"));
        fixture.PullClaimLogs.AnyMessageContains(deliveryId.ToString()).ShouldBeTrue();
    }

    [Fact]
    public async Task MappingFailure_IsRecordedAsTransformAndNeverHandedOut()
    {
        Guid deliveryId = await fixture.FanoutPullDeliveryAsync("order.broken");

        (await ClaimAsync(A)).ShouldBeEmpty();

        EventDeliveryState delivery = await fixture.GetEventDeliveryAsync(deliveryId);
        delivery.Status.ShouldBe("pending");
        delivery.LifetimeAttemptCount.ShouldBe(1);
        DeliveryAttemptState attempt = (await fixture.GetDeliveryAttemptsAsync(deliveryId)).ShouldHaveSingleItem();
        attempt.Status.ShouldBe("failed");
        attempt.FailurePhase.ShouldBe("transform");
    }
}
