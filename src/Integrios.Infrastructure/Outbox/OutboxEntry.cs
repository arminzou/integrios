using System.Text.Json;

namespace Integrios.Infrastructure.Outbox;

internal sealed record OutboxEntry
{
    public required Guid Id { get; init; }
    public required Guid EventId { get; init; }
    public required JsonElement Payload { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? ProcessedAt { get; init; }
    public int AttemptCount { get; init; }
    public DateTimeOffset? DeliverAfter { get; init; }
    public string? Traceparent { get; init; }
}
