namespace Integrios.Application.Delivery;

public sealed record DeliveryExecutionOptions(
    TimeSpan HttpTimeout,
    TimeSpan AttemptDeadline,
    TimeSpan LeaseDuration,
    TimeSpan ShutdownGracePeriod)
{
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
    }
}
