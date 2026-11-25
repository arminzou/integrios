namespace Integrios.Client;

/// <summary>
/// Handles one delivery. Return <see cref="DeliveryResult.Ack"/> on success or
/// <see cref="DeliveryResult.Retry"/> to have Integrios retry it; throw
/// <see cref="PermanentDeliveryFailureException"/> to dead-letter it. Any other exception retries it
/// under the platform's backoff. The token is cancelled when the host stops or the attempt loses
/// ownership of the delivery.
/// </summary>
public interface IDeliveryHandler
{
    /// <summary>Handles the claimed delivery.</summary>
    Task<DeliveryResult> HandleAsync(Delivery delivery, CancellationToken cancellationToken);
}

/// <summary>The outcome returned by a delivery handler.</summary>
public sealed class DeliveryResult
{
    private DeliveryResult(Settlement settlement) => Settlement = settlement;

    internal Settlement Settlement { get; }

    /// <summary>Acknowledges a successful delivery.</summary>
    public static DeliveryResult Ack { get; } = new(Settlement.Ack);

    /// <summary>True for <see cref="Retry"/>, false for <see cref="Ack"/>.</summary>
    public bool IsRetry => Settlement.Action == "release";

    /// <summary>
    /// The retry delay a handler asked for, rounded up to whole seconds; null when it left the delay to the platform.
    /// </summary>
    public TimeSpan? RetryAfter => Settlement.RetryAfterSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null;

    /// <summary>The reason for the retry, if provided.</summary>
    public string? Reason => Settlement.Error;

    /// <summary>Requests that Integrios retry the delivery.</summary>
    /// <param name="after">Delay before the retry, up to 15 minutes; null uses the platform's backoff.</param>
    /// <param name="reason">The optional reason for the retry.</param>
    public static DeliveryResult Retry(TimeSpan? after = null, string? reason = null)
    {
        if (after is not { } delay)
            return new(Settlement.Release(null, reason));
        if (delay < TimeSpan.Zero || delay > Settlement.MaxRetryAfter)
            throw new ArgumentOutOfRangeException(nameof(after), delay, "A retry delay must be from zero to 15 minutes.");
        return new(Settlement.Release((int)Math.Ceiling(delay.TotalSeconds), reason));
    }
}

/// <summary>Indicates that Integrios should dead-letter the delivery.</summary>
/// <param name="message">The message describing the failure.</param>
/// <param name="innerException">The optional exception that caused the failure.</param>
public sealed class PermanentDeliveryFailureException(string message, Exception? innerException = null)
    : Exception(message, innerException);
