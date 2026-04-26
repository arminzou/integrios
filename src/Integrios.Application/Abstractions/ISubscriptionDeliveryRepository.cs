namespace Integrios.Application.Abstractions;

public interface ISubscriptionDeliveryRepository
{
    Task<int> FanoutAsync(Guid eventId, IReadOnlyList<SubscriptionFanoutTarget> targets, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SubscriptionDeliveryWorkItem>> ClaimBatchAsync(int limit, CancellationToken cancellationToken = default);
    Task MarkSucceededAsync(Guid deliveryId, CancellationToken cancellationToken = default);
    Task ScheduleRetryAsync(Guid deliveryId, int newAttemptCount, DateTimeOffset deliverAfter, CancellationToken cancellationToken = default);
    Task MarkDeadLetteredAsync(Guid deliveryId, CancellationToken cancellationToken = default);
}

public record SubscriptionFanoutTarget(Guid SubscriptionId, Guid DestinationConnectionId);

public record SubscriptionDeliveryWorkItem(
    Guid Id,
    Guid EventId,
    Guid SubscriptionId,
    Guid DestinationConnectionId,
    int AttemptCount,
    string DestinationUrl,
    string PayloadJson);
