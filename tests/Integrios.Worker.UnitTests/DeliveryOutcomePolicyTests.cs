using Integrios.Application.Delivery;

namespace Integrios.Worker.UnitTests;

public sealed class DeliveryOutcomePolicyTests
{
    private static readonly DateTimeOffset DatabaseNow = new(2026, 7, 23, 12, 0, 0, TimeSpan.Zero);
    private readonly DeliveryOutcomePolicy policy = new(new RetryPolicy());

    [Fact]
    public void Decide_Success_CompletesDeliveryWithoutBackoff()
    {
        DeliveryOutcomeDecision decision = policy.Decide(DeliveryOutcomeKind.Succeeded, 1, DatabaseNow);

        Assert.Equal(SubscriptionDeliveryDisposition.Succeeded, decision.Disposition);
        Assert.Null(decision.DeliverAfter);
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

        Assert.Equal(SubscriptionDeliveryDisposition.RetryScheduled, decision.Disposition);
        Assert.Equal(DatabaseNow.AddSeconds(expectedSeconds), decision.DeliverAfter);
    }

    [Theory]
    [InlineData(DeliveryOutcomeKind.Failed)]
    [InlineData(DeliveryOutcomeKind.Indeterminate)]
    public void Decide_ConsumedRetryBudget_DeadLetters(DeliveryOutcomeKind outcome)
    {
        DeliveryOutcomeDecision decision = policy.Decide(outcome, RetryPolicy.DefaultMaxAttempts, DatabaseNow);

        Assert.Equal(SubscriptionDeliveryDisposition.DeadLettered, decision.Disposition);
        Assert.Null(decision.DeliverAfter);
    }

    [Fact]
    public void Decide_IndeterminateWithinRetryBudget_IsImmediatelyEligibleAndConsumesCurrentSlot()
    {
        DeliveryOutcomeDecision decision = policy.Decide(DeliveryOutcomeKind.Indeterminate, 2, DatabaseNow);

        Assert.Equal(SubscriptionDeliveryDisposition.RetryScheduled, decision.Disposition);
        Assert.Equal(DatabaseNow, decision.DeliverAfter);
    }

}
