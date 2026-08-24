using System.Text.Json;
using Integrios.Domain.Entities;
using Integrios.Domain.Enums;

namespace Integrios.Application.Ingestion;

public interface IEventAcceptance
{
    Task<EventAcceptance> AcceptAsync(
        EventSubmission submission,
        string? traceparent,
        CancellationToken cancellationToken);
}

public sealed record EventSubmission
{
    public required Guid TenantId { get; init; }
    public required Guid TopicId { get; init; }
    public required Guid SourceId { get; init; }
    public string? SourceEventId { get; init; }
    public required string EventType { get; init; }
    public required JsonElement Payload { get; init; }
    public JsonElement? Metadata { get; init; }
    public string? IdempotencyKey { get; init; }
}

public sealed record EventAcceptance
{
    public required Guid EventId { get; init; }
    public required EventStatus Status { get; init; }
    public required DateTimeOffset AcceptedAt { get; init; }
    public required bool AlreadyAccepted { get; init; }
}
