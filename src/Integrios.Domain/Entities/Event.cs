using System.Text.Json;
using Integrios.Domain.Enums;

namespace Integrios.Domain.Entities;

public sealed record Event
{
    public required Guid Id { get; init; }
    public required Guid TenantId { get; init; }
    public Guid? TopicId { get; init; }
    public Guid? SourceId { get; init; }
    public string? SourceEventId { get; init; }
    public required string EventType { get; init; }
    public required JsonElement Payload { get; init; }
    public JsonElement? Metadata { get; init; }
    public string? IdempotencyKey { get; init; }
    public EventStatus Status { get; init; } = EventStatus.Accepted;
    public required DateTimeOffset AcceptedAt { get; init; }
    public DateTimeOffset? ProcessedAt { get; init; }
    public DateTimeOffset? FailedAt { get; init; }
}
