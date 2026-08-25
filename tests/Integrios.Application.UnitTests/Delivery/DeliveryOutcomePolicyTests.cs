using Integrios.Application.Delivery;

namespace Integrios.Application.UnitTests;

public sealed class DeliveryOutcomePolicyTests
{
    private static readonly DateTimeOffset DatabaseNow = new(2026, 7, 23, 12, 0, 0, TimeSpan.Zero);
    private readonly DeliveryOutcomePolicy policy = new(new RetryPolicy());

    [Fact]
    public void Decide_Success_CompletesDeliveryWithoutBackoff()
    {
        DeliveryOutcomeDecision decision = policy.Decide(DeliveryOutcomeKind.Succeeded, 1, DatabaseNow);

        decision.Disposition.ShouldBe(EventDeliveryDisposition.Succeeded);
        decision.DeliverAfter.ShouldBeNull();
    }

    [Theory]
    [InlineData(1, 30)]
    [InlineData(2, 60)]
    public void Decide_FailureWithinRetryBudget_SchedulesPolicyBackoff(int retryCycleAttemptCount, int expectedSeconds)
    {
        DeliveryOutcomeDecision decision = policy.Decide(
            DeliveryOutcomeKind.Failed,
            retryCycleAttemptCount,
            DatabaseNow);

        decision.Disposition.ShouldBe(EventDeliveryDisposition.RetryScheduled);
        decision.DeliverAfter.ShouldBe(DatabaseNow.AddSeconds(expectedSeconds));
    }

    [Theory]
    [InlineData(DeliveryOutcomeKind.Failed)]
    [InlineData(DeliveryOutcomeKind.Indeterminate)]
    public void Decide_ConsumedRetryBudget_DeadLetters(DeliveryOutcomeKind outcome)
    {
        DeliveryOutcomeDecision decision = policy.Decide(outcome, RetryPolicy.DefaultMaxAttempts, DatabaseNow);

        decision.Disposition.ShouldBe(EventDeliveryDisposition.DeadLettered);
        decision.DeliverAfter.ShouldBeNull();
    }

    [Fact]
    public void Decide_IndeterminateWithinRetryBudget_IsImmediatelyEligibleAndConsumesCurrentSlot()
    {
        DeliveryOutcomeDecision decision = policy.Decide(DeliveryOutcomeKind.Indeterminate, 2, DatabaseNow);

        decision.Disposition.ShouldBe(EventDeliveryDisposition.RetryScheduled);
        decision.DeliverAfter.ShouldBe(DatabaseNow);
    }

    [Fact]
    public void Decide_TerminalFailure_DeadLettersImmediatelyRegardlessOfRemainingBudget()
    {
        DeliveryOutcomeDecision decision = policy.Decide(
            DeliveryOutcomeKind.Failed, retryCycleAttemptCount: 1, DatabaseNow, isTerminal: true);

        decision.Disposition.ShouldBe(EventDeliveryDisposition.DeadLettered);
        decision.DeliverAfter.ShouldBeNull();
    }

    [Fact]
    public void Decide_TransientFailureWithRetryAfter_HonorsItOverExponentialBackoff()
    {
        DeliveryOutcomeDecision decision = policy.Decide(
            DeliveryOutcomeKind.Failed, retryCycleAttemptCount: 1, DatabaseNow, retryAfter: TimeSpan.FromSeconds(7));

        decision.Disposition.ShouldBe(EventDeliveryDisposition.RetryScheduled);
        decision.DeliverAfter.ShouldBe(DatabaseNow.AddSeconds(7));
    }
}
