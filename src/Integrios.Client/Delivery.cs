using System.Text.Json;

namespace Integrios.Client;

public sealed record Delivery(
    Guid DeliveryId,
    string LeaseToken,
    int AttemptNumber,
    Guid EventId,
    string EventType,
    string? Topic,
    DateTimeOffset AcceptedAt,
    JsonElement Payload,
    DateTimeOffset LeaseExpiresAt,
    string? Traceparent);
