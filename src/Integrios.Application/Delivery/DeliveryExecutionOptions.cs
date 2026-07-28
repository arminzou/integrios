namespace Integrios.Application.Delivery;

public sealed record DeliveryExecutionOptions(
    TimeSpan HttpTimeout,
    TimeSpan AttemptDeadline,
    TimeSpan LeaseDuration,
    TimeSpan ShutdownGracePeriod)
{
    // Cadence settings are init-only rather than positional so that adding one does not break
    // every existing construction site.
    public TimeSpan IdlePollInterval { get; init; } = TimeSpan.FromSeconds(2);

    public TimeSpan RetryBaseDelay { get; init; } = RetryPolicy.DefaultBaseDelay;

    public int RetryMaxAttempts { get; init; } = RetryPolicy.DefaultMaxAttempts;

    public static DeliveryExecutionOptions Default { get; } = new(
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(45),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromSeconds(60));

    public void Validate()
    {
        if (HttpTimeout <= TimeSpan.Zero)
            throw new InvalidOperationException("Integrios:Delivery:HttpTimeout must be positive.");
        if (AttemptDeadline <= HttpTimeout)
            throw new InvalidOperationException("Integrios:Delivery:AttemptDeadline must be greater than HttpTimeout.");
        if (LeaseDuration <= AttemptDeadline)
            throw new InvalidOperationException("Integrios:Delivery:LeaseDuration must be greater than AttemptDeadline.");
        if (ShutdownGracePeriod <= AttemptDeadline)
            throw new InvalidOperationException("Integrios:Delivery:ShutdownGracePeriod must be greater than AttemptDeadline.");
        if (IdlePollInterval <= TimeSpan.Zero)
            throw new InvalidOperationException("Integrios:Delivery:IdlePollInterval must be positive.");
        if (RetryBaseDelay <= TimeSpan.Zero)
            throw new InvalidOperationException("Integrios:Delivery:Retry:BaseDelay must be positive.");
        if (RetryMaxAttempts < 1)
            throw new InvalidOperationException("Integrios:Delivery:Retry:MaxAttempts must be at least 1.");
    }
}
