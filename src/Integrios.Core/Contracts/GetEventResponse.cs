using Integrios.Core.Domain.Events;

namespace Integrios.Core.Contracts;

public sealed record GetEventResponse
{
    public required Guid EventId { get; init; }
    public required EventStatus Status { get; init; }
    public required DateTimeOffset AcceptedAt { get; init; }
    public DateTimeOffset? ProcessedAt { get; init; }
    public DateTimeOffset? FailedAt { get; init; }
    public IReadOnlyList<DeliveryAttemptSummary> DeliveryAttempts { get; init; } = [];
}
