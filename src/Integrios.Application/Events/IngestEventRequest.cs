using System.Text.Json;

namespace Integrios.Application.Events;

public sealed record IngestEventRequest
{
    // Upstream provider event identifier for traceability only.
    // Duplicate suppression is driven by IdempotencyKey.
    public string? SourceEventId { get; init; }
    public required string EventType { get; init; }
    public required JsonElement Payload { get; init; }
    public JsonElement? Metadata { get; init; }
    // Tenant-scoped deduplication key for acceptance-boundary idempotency.
    public string? IdempotencyKey { get; init; }
    // Named topic this event targets. If provided and resolved, topic_id is stored on the event.
    public string? TopicName { get; init; }
}
