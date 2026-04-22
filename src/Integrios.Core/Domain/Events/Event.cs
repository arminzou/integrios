using System.Text.Json;

namespace Integrios.Core.Domain.Events;

public sealed record Event
{
    public required Guid Id { get; init; }
    public required Guid TenantId { get; init; }
    public Guid? PipelineId { get; init; }
    public Guid? SourceConnectionId { get; init; }
    public string? SourceEventId { get; init; }
    public required string EventType { get; init; }
    public required JsonElement Payload { get; init; }
    public JsonElement? Metadata { get; init; }
    public string? IdempotencyKey { get; init; }
    public required EventStatus Status { get; init; }
    public required DateTimeOffset AcceptedAt { get; init; }
    public DateTimeOffset? ProcessedAt { get; init; }
    public DateTimeOffset? FailedAt { get; init; }
}
