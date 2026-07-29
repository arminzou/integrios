namespace Integrios.Application.Delivery;

public enum DeliveryOutcomeKind
{
    Succeeded = 0,
    Failed = 1,
    Indeterminate = 2
}

public sealed record DeliveryOutcomeDecision(
    SubscriptionDeliveryDisposition Disposition,
    DateTimeOffset? DeliverAfter = null);

public sealed class DeliveryOutcomePolicy(RetryPolicy retryPolicy)
{
    public DeliveryOutcomeDecision Decide(
        DeliveryOutcomeKind outcome,
        int retryCycleAttemptCount,
        DateTimeOffset databaseNow)
    {
        if (outcome == DeliveryOutcomeKind.Succeeded)
            return new(SubscriptionDeliveryDisposition.Succeeded);

        if (retryCycleAttemptCount >= retryPolicy.MaxAttempts)
            return new(SubscriptionDeliveryDisposition.DeadLettered);

        var deliverAfter = outcome == DeliveryOutcomeKind.Indeterminate
            ? databaseNow
            : databaseNow + retryPolicy.CalculateBackoff(retryCycleAttemptCount);

        return new(SubscriptionDeliveryDisposition.RetryScheduled, deliverAfter);
    }
}
