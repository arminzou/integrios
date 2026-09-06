using System.Text.Json;
using Integrios.Application.Ingestion;
using Integrios.Domain.Enums;

namespace Integrios.Application.Delivery;

/// <summary>
/// What an Operator needs to diagnose a Delivery, including the bodies involved.
/// </summary>
/// <remarks>
/// Deliberately separate from <see cref="EventDto"/>, which the data plane returns to a Tenant
/// holding a TenantApiKey. A destination's response body is the Operator's downstream system
/// talking — internal error text, hostnames, identifiers belonging to no Tenant — so it must never
/// be reachable from the data plane. Keeping the two shapes apart makes that structural: the
/// Tenant-facing type has no field to leak, and this one is only registered in the Admin host.
/// </remarks>
public sealed record EventDiagnosticsDto
{
    public required Guid EventId { get; init; }
    public required EventStatus Status { get; init; }
    public required DateTimeOffset AcceptedAt { get; init; }
    public DateTimeOffset? ProcessedAt { get; init; }
    public DateTimeOffset? FailedAt { get; init; }
    public string? TraceId { get; init; }
    public string? EventType { get; init; }
    public JsonElement? Payload { get; init; }
    public JsonElement? Metadata { get; init; }
    public IReadOnlyList<EventDeliveryDto> EventDeliveries { get; init; } = [];
    public IReadOnlyList<DeliveryAttemptDiagnosticsDto> DeliveryAttempts { get; init; } = [];
}

public sealed record DeliveryAttemptDiagnosticsDto
{
    public required Guid AttemptId { get; init; }
    public required Guid EventDeliveryId { get; init; }
    public required Guid SubscriptionId { get; init; }
    public required Guid DestinationConnectionId { get; init; }
    public required int AttemptNumber { get; init; }
    public required string Status { get; init; }
    public string? FailurePhase { get; init; }
    public int? ResponseStatusCode { get; init; }
    public string? ErrorMessage { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>What was actually sent, after any Subscription mapping.</summary>
    public JsonElement? RequestPayload { get; init; }

    /// <summary>What the destination returned, bounded at capture.</summary>
    public string? ResponseBody { get; init; }

    /// <summary>
    /// Whether the stored body is only the leading part of what the destination returned. Reported
    /// rather than inferred from length, so a reader is never shown a fragment as if it were whole.
    /// </summary>
    public bool ResponseBodyTruncated { get; init; }
}
