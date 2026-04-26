using Integrios.Domain.Events;

namespace Integrios.Application.Events;

public sealed record GetEventResponse
{
    public required Guid EventId { get; init; }
    public required EventStatus Status { get; init; }
    public required DateTimeOffset AcceptedAt { get; init; }
    public DateTimeOffset? ProcessedAt { get; init; }
    public DateTimeOffset? FailedAt { get; init; }
    public IReadOnlyList<DeliveryAttemptSummary> DeliveryAttempts { get; init; } = [];
}

public sealed record DeliveryAttemptSummary
{
    public required Guid SubscriptionId { get; init; }
    public required Guid DestinationConnectionId { get; init; }
    public required int AttemptNumber { get; init; }
    public required string Status { get; init; }
    public int? ResponseStatusCode { get; init; }
    public string? ErrorMessage { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
}
