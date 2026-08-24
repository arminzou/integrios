using Integrios.Domain.Entities;
using Integrios.Domain.Enums;

namespace Integrios.Application.Ingestion;

public sealed record EventDto
{
    public required Guid EventId { get; init; }
    public required EventStatus Status { get; init; }
    public required DateTimeOffset AcceptedAt { get; init; }
    public DateTimeOffset? ProcessedAt { get; init; }
    public DateTimeOffset? FailedAt { get; init; }
    public IReadOnlyList<EventDeliveryDto> EventDeliveries { get; init; } = [];
    public IReadOnlyList<DeliveryAttemptDto> DeliveryAttempts { get; init; } = [];
}

public sealed record EventDeliveryDto
{
    public required Guid EventDeliveryId { get; init; }
    public required Guid SubscriptionId { get; init; }
    public required Guid DestinationConnectionId { get; init; }
    public required string Status { get; init; }
    public required int LifetimeAttemptCount { get; init; }
    public required int RetryCycleAttemptCount { get; init; }
    public DateTimeOffset? DeliverAfter { get; init; }
    public DateTimeOffset? FailedAt { get; init; }
}

public sealed record DeliveryAttemptDto
{
    public required Guid AttemptId { get; init; }
    public required Guid EventDeliveryId { get; init; }
    public required Guid SubscriptionId { get; init; }
    public required Guid DestinationConnectionId { get; init; }
    public required int AttemptNumber { get; init; }
    public required string Status { get; init; }
    public string? FailurePhase { get; init; }
    public int? ResponseStatusCode { get; init; }
    public string? ErrorMessage { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
}
