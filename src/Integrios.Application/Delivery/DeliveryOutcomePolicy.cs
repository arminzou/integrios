namespace Integrios.Application.Delivery;

public enum DeliveryOutcomeKind
{
    Succeeded = 0,
    Failed = 1,
    Indeterminate = 2
}

public sealed record DeliveryOutcomeDecision(
    EventDeliveryDisposition Disposition,
    DateTimeOffset? DeliverAfter = null);

public sealed class DeliveryOutcomePolicy(RetryPolicy retryPolicy)
{
    public DeliveryOutcomeDecision Decide(
        DeliveryOutcomeKind outcome,
        int retryCycleAttemptCount,
        DateTimeOffset databaseNow,
        bool isTerminal = false,
        TimeSpan? retryAfter = null)
    {
        if (outcome == DeliveryOutcomeKind.Succeeded)
            return new(EventDeliveryDisposition.Succeeded);

        if (isTerminal || retryCycleAttemptCount >= retryPolicy.MaxAttempts)
            return new(EventDeliveryDisposition.DeadLettered);

        var deliverAfter = outcome == DeliveryOutcomeKind.Indeterminate
            ? databaseNow
            : databaseNow + (retryAfter ?? retryPolicy.CalculateBackoff(retryCycleAttemptCount));

        return new(EventDeliveryDisposition.RetryScheduled, deliverAfter);
    }
}
