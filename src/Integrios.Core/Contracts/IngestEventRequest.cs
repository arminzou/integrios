using System.Text.Json;

namespace Integrios.Core.Contracts;

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
}
