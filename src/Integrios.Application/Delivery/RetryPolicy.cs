namespace Integrios.Application.Delivery;

public sealed class RetryPolicy
{
    public const int DefaultMaxAttempts = 3;

    private static readonly TimeSpan BaseDelay = TimeSpan.FromSeconds(30);
    private const int MaxExponent = 10;

    public TimeSpan CalculateBackoff(int attemptCount)
    {
        var exponent = Math.Min(attemptCount - 1, MaxExponent);
        return BaseDelay * Math.Pow(2, exponent);
    }
}
