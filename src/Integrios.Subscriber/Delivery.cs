using System.Text.Json;

namespace Integrios.Subscriber;

public sealed record Delivery(
    Guid DeliveryId,
    Guid AttemptId,
    int AttemptNumber,
    Guid EventId,
    string EventType,
    string? Topic,
    DateTimeOffset AcceptedAt,
    JsonElement Payload,
    DateTimeOffset LeaseExpiresAt,
    string? Traceparent);
