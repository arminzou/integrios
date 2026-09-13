using Integrios.Domain.Enums;

namespace Integrios.Application.Ingestion;

public sealed record EventListItemDto
{
    public required Guid EventId { get; init; }
    public Guid? SourceId { get; init; }
    public Guid? TopicId { get; init; }
    public string? SourceEventId { get; init; }
    public required string EventType { get; init; }
    public required EventStatus Status { get; init; }
    public required DateTimeOffset AcceptedAt { get; init; }
    public string? TraceId { get; init; }
    public required EventDeliveryCounts Deliveries { get; init; }
}

/// EventDelivery state summarized per Event; a non-zero dead-lettered count is Delivery state, not Event status.
public sealed record EventDeliveryCounts(int Pending, int InFlight, int Succeeded, int DeadLettered);
