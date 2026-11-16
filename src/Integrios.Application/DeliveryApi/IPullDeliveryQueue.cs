using Integrios.Domain.Enums;

namespace Integrios.Application.DeliveryApi;

public interface IPullDeliveryQueue
{
    Task<PullDeliveryWorkItem?> ClaimNextAsync(Guid destinationId, CancellationToken cancellationToken);

    Task<PullLeaseRenewalResult> RenewAsync(PullAttemptOwner owner, CancellationToken cancellationToken);

    Task<PullSettlementStatus> SettleAsync(
        PullAttemptOwner owner,
        PullSettlement settlement,
        CancellationToken cancellationToken);
}

public sealed record PullDeliveryWorkItem(
    Guid Id,
    Guid AttemptId,
    int AttemptNumber,
    Guid EventId,
    Guid SubscriptionId,
    string PayloadJson,
    string EventType,
    string? TopicName,
    DateTimeOffset AcceptedAt,
    string? MappingConfigSnapshot,
    DateTimeOffset LeaseExpiresAt,
    string? Traceparent);

public sealed record PullAttemptOwner(Guid DestinationId, Guid DeliveryId, Guid AttemptId);

public sealed record PullSettlement(
    bool Succeeded,
    bool IsTerminal,
    DeliveryFailurePhase? FailurePhase,
    string? ErrorMessage,
    TimeSpan? RetryAfter)
{
    public static PullSettlement Ack() => new(true, false, null, null, null);

    public static PullSettlement Nack(string? errorMessage, TimeSpan? retryAfter) =>
        new(false, false, DeliveryFailurePhase.Subscriber, errorMessage, retryAfter);

    public static PullSettlement Fail(string errorMessage) =>
        new(false, true, DeliveryFailurePhase.Subscriber, errorMessage, null);

    public static PullSettlement MappingFailed(string errorMessage) =>
        new(false, false, DeliveryFailurePhase.Transform, errorMessage, null);
}

public enum PullSettlementStatus
{
    Applied = 0,
    AlreadyApplied = 1,
    OwnershipLost = 2
}

public enum PullLeaseRenewalStatus
{
    Renewed = 0,
    OwnershipLost = 1,
    MaxAttemptDurationReached = 2
}

public sealed record PullLeaseRenewalResult(PullLeaseRenewalStatus Status, DateTimeOffset? LeaseExpiresAt);
