using Integrios.Domain.Events;

namespace Integrios.Application.Events;

public sealed record IngestEventResult
{
    public required Guid EventId { get; init; }
    public required EventStatus Status { get; init; }
    public required DateTimeOffset AcceptedAt { get; init; }
    public required bool AlreadyAccepted { get; init; }
}
