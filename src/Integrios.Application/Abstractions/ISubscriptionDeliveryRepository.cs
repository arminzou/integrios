using Integrios.Domain.Integrations;

namespace Integrios.Application.Abstractions;

public interface ISubscriptionDeliveryRepository
{
    Task<IReadOnlyList<SubscriptionDeliveryWorkItem>> ClaimBatchAsync(int limit, CancellationToken cancellationToken = default);
    Task MarkSucceededAsync(Guid deliveryId, CancellationToken cancellationToken = default);
    Task ScheduleRetryAsync(Guid deliveryId, int newAttemptCount, DateTimeOffset deliverAfter, CancellationToken cancellationToken = default);
    Task MarkDeadLetteredAsync(Guid deliveryId, CancellationToken cancellationToken = default);
}

public record SubscriptionDeliveryWorkItem(
    Guid Id,
    Guid EventId,
    Guid SubscriptionId,
    Guid DestinationConnectionId,
    Guid TenantId,
    int AttemptCount,
    string DestinationUrl,
    string PayloadJson,
    string EventType,
    string? TopicName,
    DateTimeOffset AcceptedAt,
    string? TransformConfigSnapshot,
    string IntegrationKey,
    ConnectionAuth? DestinationAuth,
    string? Traceparent);
