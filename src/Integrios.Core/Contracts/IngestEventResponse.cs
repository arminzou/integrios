using Integrios.Core.Domain.Events;

namespace Integrios.Core.Contracts;

public sealed record IngestEventResponse
{
    public required Guid EventId { get; init; }
    public required EventStatus Status { get; init; }
    public required DateTimeOffset AcceptedAt { get; init; }
    public required bool IsDuplicate { get; init; }
}
