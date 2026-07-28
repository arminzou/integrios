namespace Integrios.Application.Delivery;

public sealed class RetryPolicy
{
    public const int DefaultMaxAttempts = 3;
    public static readonly TimeSpan DefaultBaseDelay = TimeSpan.FromSeconds(30);

    private const int MaxExponent = 10;

    public RetryPolicy()
        : this(DefaultBaseDelay, DefaultMaxAttempts)
    {
    }

    public RetryPolicy(TimeSpan baseDelay, int maxAttempts)
    {
        BaseDelay = baseDelay;
        MaxAttempts = maxAttempts;
    }

    public TimeSpan BaseDelay { get; }

    public int MaxAttempts { get; }

    public TimeSpan CalculateBackoff(int attemptCount)
    {
        var exponent = Math.Min(attemptCount - 1, MaxExponent);
        return BaseDelay * Math.Pow(2, exponent);
    }
}
