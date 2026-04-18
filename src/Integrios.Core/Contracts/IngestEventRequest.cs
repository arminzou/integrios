using System.Text.Json;

namespace Integrios.Core.Contracts;

public sealed record IngestEventRequest
{
    public string? SourceEventId { get; init; }
    public required string EventType { get; init; }
    public required JsonElement Payload { get; init; }
    public JsonElement? Metadata { get; init; }
    public string? IdempotencyKey { get; init; }
}
