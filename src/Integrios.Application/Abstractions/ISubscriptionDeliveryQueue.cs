using Integrios.Domain.Delivery;

namespace Integrios.Application.Abstractions;

public interface ISubscriptionDeliveryQueue
{
    Task<SubscriptionDeliveryWorkItem?> ClaimNextAsync(CancellationToken cancellationToken = default);

    Task<DeliveryFinalizationResult> FinalizeAsync(
        DeliveryAttemptCompletion completion,
        CancellationToken cancellationToken = default);

    Task<bool> ReplayDeadLetteredAsync(
        Guid tenantId,
        Guid eventId,
        CancellationToken cancellationToken = default);
}

public sealed record SubscriptionDeliveryWorkItem(
    Guid Id,
    Guid AttemptId,
    int AttemptNumber,
    Guid EventId,
    Guid SubscriptionId,
    Guid DestinationConnectionId,
    Guid TenantId,
    string DestinationUrl,
    string PayloadJson,
    string EventType,
    string? TopicName,
    DateTimeOffset AcceptedAt,
    string? TransformConfigSnapshot,
    string IntegrationKey,
    string? DestinationAuthJson,
    string? Traceparent);

public sealed record DeliveryAttemptCompletion(
    Guid DeliveryId,
    Guid AttemptId,
    bool Succeeded,
    DeliveryFailurePhase? FailurePhase,
    string? RequestPayloadJson,
    int? ResponseStatusCode,
    string? ResponseBody,
    string? ErrorMessage);

public enum DeliveryFinalizationStatus
{
    Applied = 0,
    OwnershipLost = 1
}

public enum SubscriptionDeliveryDisposition
{
    Succeeded = 0,
    RetryScheduled = 1,
    DeadLettered = 2
}

public sealed record DeliveryFinalizationResult(
    DeliveryFinalizationStatus Status,
    SubscriptionDeliveryDisposition? Disposition = null);
