using Integrios.Domain.Delivery;

namespace Integrios.Application.Delivery;

public interface ISubscriptionDeliveryQueue
{
    Task<SubscriptionDeliveryClaimResult?> ClaimNextWithRecoveryAsync(
        CancellationToken cancellationToken);

    Task<DeliveryFinalizationResult> FinalizeAsync(
        DeliveryAttemptCompletion completion,
        CancellationToken cancellationToken);
}

public abstract record SubscriptionDeliveryClaimResult;

public sealed record ClaimedSubscriptionDelivery(SubscriptionDeliveryWorkItem WorkItem)
    : SubscriptionDeliveryClaimResult;

public sealed record RecoveredSubscriptionDeliveryDeadLetter(
    Guid DeliveryId,
    Guid AttemptId,
    int AttemptNumber,
    Guid EventId,
    Guid SubscriptionId,
    string IntegrationKey)
    : SubscriptionDeliveryClaimResult;

public sealed record SubscriptionDeliveryWorkItem(
    Guid Id,
    Guid AttemptId,
    int AttemptNumber,
    Guid EventId,
    Guid SubscriptionId,
    Guid DestinationConnectionId,
    Guid TenantId,
    string TenantSlug,
    string PayloadJson,
    string EventType,
    string? TopicName,
    DateTimeOffset AcceptedAt,
    string? TransformConfigSnapshot,
    string IntegrationKey,
    string HttpExecutionSnapshotJson,
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
