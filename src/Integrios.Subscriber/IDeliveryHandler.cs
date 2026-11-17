namespace Integrios.Subscriber;

/// Handles one delivery. Return <see cref="DeliveryResult.Ack"/> on success or
/// <see cref="DeliveryResult.Retry"/> to have Integrios retry it; throw
/// <see cref="PermanentDeliveryFailureException"/> to dead-letter it. Any other exception retries it
/// under the platform's backoff. The token is cancelled when the host stops or the attempt loses
/// ownership of the delivery.
public interface IDeliveryHandler
{
    Task<DeliveryResult> HandleAsync(Delivery delivery, CancellationToken cancellationToken);
}

public sealed class DeliveryResult
{
    private DeliveryResult(Settlement settlement) => Settlement = settlement;

    internal Settlement Settlement { get; }

    public static DeliveryResult Ack { get; } = new(Settlement.Ack);

    /// <param name="after">Delay before the retry, up to 15 minutes; null uses the platform's backoff.</param>
    public static DeliveryResult Retry(TimeSpan? after = null, string? reason = null)
    {
        if (after is not { } delay)
            return new(Settlement.Nack(null, reason));
        if (delay < TimeSpan.Zero || delay > Settlement.MaxRetryAfter)
            throw new ArgumentOutOfRangeException(nameof(after), delay, "A retry delay must be from zero to 15 minutes.");
        return new(Settlement.Nack((int)Math.Ceiling(delay.TotalSeconds), reason));
    }
}

public sealed class PermanentDeliveryFailureException(string message, Exception? innerException = null)
    : Exception(message, innerException);
