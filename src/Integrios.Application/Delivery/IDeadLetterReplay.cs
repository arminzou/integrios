namespace Integrios.Application.Delivery;

public interface IDeadLetterReplay
{
    Task<bool> ReplayDeadLetteredAsync(
        Guid tenantId,
        Guid eventId,
        CancellationToken cancellationToken);
}
