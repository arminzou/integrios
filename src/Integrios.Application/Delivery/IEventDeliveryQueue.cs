using Integrios.Domain.Entities;
using Integrios.Domain.Enums;

namespace Integrios.Application.Delivery;

public interface IEventDeliveryQueue
{
    Task<EventDeliveryClaimResult?> ClaimNextWithRecoveryAsync(
        CancellationToken cancellationToken);

    Task<DeliveryFinalizationResult> FinalizeAsync(
        DeliveryAttemptCompletion completion,
        CancellationToken cancellationToken);
}

public abstract record EventDeliveryClaimResult;

public sealed record ClaimedEventDelivery(EventDeliveryWorkItem WorkItem)
    : EventDeliveryClaimResult;

public sealed record RecoveredEventDeliveryDeadLetter(
    Guid DeliveryId,
    Guid AttemptId,
    int AttemptNumber,
    Guid EventId,
    Guid SubscriptionId,
    string ConnectorKey)
    : EventDeliveryClaimResult;

public sealed record EventDeliveryWorkItem(
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
    string? MappingConfigSnapshot,
    string ConnectorKey,
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
    string? ErrorMessage,
    bool IsTerminalFailure = false,
    TimeSpan? RetryAfter = null);

public enum DeliveryFinalizationStatus
{
    Applied = 0,
    OwnershipLost = 1
}

public enum EventDeliveryDisposition
{
    Succeeded = 0,
    RetryScheduled = 1,
    DeadLettered = 2
}

public sealed record DeliveryFinalizationResult(
    DeliveryFinalizationStatus Status,
    EventDeliveryDisposition? Disposition = null);
