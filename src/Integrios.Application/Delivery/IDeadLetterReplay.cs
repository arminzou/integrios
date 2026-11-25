namespace Integrios.Application.Delivery;

public interface IDeadLetterReplay
{
    Task<DeadLetterReplayResult> ReplayAsync(
        Guid tenantId,
        Guid eventId,
        Guid eventDeliveryId,
        CancellationToken cancellationToken);
}

public enum DeadLetterReplayResult
{
    Replayed,
    NotFound,
    NotDeadLettered
}
