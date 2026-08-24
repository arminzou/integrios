using Integrios.Domain.Entities;
using Integrios.Domain.Enums;

namespace Integrios.Application.Ingestion;

public sealed record IngestEventResult
{
    public required Guid EventId { get; init; }
    public required EventStatus Status { get; init; }
    public required DateTimeOffset AcceptedAt { get; init; }
    public required bool AlreadyAccepted { get; init; }
}
