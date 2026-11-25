using System.Text.Json;

namespace Integrios.Client;

/// <summary>A delivery claimed from the Delivery API.</summary>
public sealed record Delivery
{
    /// <summary>The delivery's identifier.</summary>
    public required Guid DeliveryId { get; init; }

    /// <summary>The lease token for this attempt.</summary>
    public required string LeaseToken { get; init; }

    /// <summary>The delivery attempt number.</summary>
    public required int AttemptNumber { get; init; }

    /// <summary>The event's identifier.</summary>
    public required Guid EventId { get; init; }

    /// <summary>The event's type.</summary>
    public required string EventType { get; init; }

    /// <summary>The topic name, if present.</summary>
    public string? Topic { get; init; }

    /// <summary>When the event was accepted.</summary>
    public required DateTimeOffset AcceptedAt { get; init; }

    /// <summary>The JSON payload of this delivery.</summary>
    public required JsonElement Payload { get; init; }

    /// <summary>When the claimed lease expires.</summary>
    public required DateTimeOffset LeaseExpiresAt { get; init; }

    /// <summary>The traceparent value, if present.</summary>
    public string? Traceparent { get; init; }
}
