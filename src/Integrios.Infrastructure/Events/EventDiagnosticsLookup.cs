using System.Text.Json;
using Dapper;
using Integrios.Application.Delivery;
using Integrios.Application.Ingestion;
using Integrios.Application.Telemetry;
using Integrios.Domain.Enums;
using Integrios.Infrastructure.Data;

namespace Integrios.Infrastructure.Events;

/// <remarks>
/// Reads much of what <see cref="TenantEventLookup"/> reads, and deliberately does not share its
/// query. The two serve opposite sides of the plane split, so a single parameterised reader would
/// put the Operator's diagnostic columns one argument away from the data plane. The duplication is
/// the price of that separation being structural rather than remembered.
/// </remarks>
internal sealed class EventDiagnosticsLookup(IDbConnectionFactory connectionFactory)
    : IEventDiagnosticsLookup
{
    public async Task<EventDiagnosticsDto?> GetAsync(
        Guid tenantId,
        Guid eventId,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        bool sqlServer = connectionFactory.Provider == DatabaseProvider.SqlServer;
        string top = sqlServer ? "TOP (1) " : string.Empty;
        string limit = sqlServer ? string.Empty : "LIMIT 1;";
        string payload = sqlServer ? "payload" : "payload::text";
        string metadata = sqlServer ? "metadata" : "metadata::text";

        var row = await connection.QuerySingleOrDefaultAsync<EventRow>(
            new CommandDefinition(
                $"""
                SELECT
                    {top}id      AS Id,
                    status       AS Status,
                    event_type   AS EventType,
                    accepted_at  AS AcceptedAt,
                    processed_at AS ProcessedAt,
                    failed_at    AS FailedAt,
                    {payload}    AS PayloadJson,
                    {metadata}   AS MetadataJson,
                    (SELECT traceparent FROM outbox WHERE event_id = events.id) AS Traceparent
                FROM events
                WHERE tenant_id = @TenantId
                  AND id = @EventId
                {limit}
                """,
                new { TenantId = tenantId, EventId = eventId },
                cancellationToken: cancellationToken));

        if (row is null)
            return null;

        var deliveries = await connection.QueryAsync<DeliveryRow>(
            new CommandDefinition(
                """
                SELECT
                    id                        AS EventDeliveryId,
                    subscription_id           AS SubscriptionId,
                    destination_connection_id AS DestinationConnectionId,
                    status                    AS Status,
                    lifetime_attempt_count    AS LifetimeAttemptCount,
                    retry_cycle_attempt_count AS RetryCycleAttemptCount,
                    deliver_after             AS DeliverAfter,
                    failed_at                 AS FailedAt
                FROM event_deliveries
                WHERE event_id = @EventId
                ORDER BY id;
                """,
                new { EventId = eventId },
                cancellationToken: cancellationToken));

        string requestPayload = sqlServer ? "da.request_payload" : "da.request_payload::text";

        var attempts = await connection.QueryAsync<AttemptRow>(
            new CommandDefinition(
                $"""
                SELECT
                    da.id                        AS AttemptId,
                    sd.id                        AS EventDeliveryId,
                    sd.subscription_id           AS SubscriptionId,
                    sd.destination_connection_id AS DestinationConnectionId,
                    da.attempt_number            AS AttemptNumber,
                    da.status                    AS Status,
                    da.failure_phase             AS FailurePhase,
                    da.response_status_code      AS ResponseStatusCode,
                    da.error_message             AS ErrorMessage,
                    da.started_at                AS StartedAt,
                    da.completed_at              AS CompletedAt,
                    {requestPayload}             AS RequestPayloadJson,
                    da.response_body             AS ResponseBody,
                    da.response_body_truncated   AS ResponseBodyTruncated
                FROM delivery_attempts da
                JOIN event_deliveries sd ON sd.id = da.event_delivery_id
                WHERE sd.event_id = @EventId
                ORDER BY sd.id, da.attempt_number, da.started_at;
                """,
                new { EventId = eventId },
                cancellationToken: cancellationToken));

        return new EventDiagnosticsDto
        {
            EventId = row.Id,
            Status = EventStatusMap.FromDbValue(row.Status),
            EventType = row.EventType,
            AcceptedAt = row.AcceptedAt,
            ProcessedAt = row.ProcessedAt,
            FailedAt = row.FailedAt,
            Payload = Parse(row.PayloadJson),
            Metadata = Parse(row.MetadataJson),
            TraceId = ActivitySources.TryParseTraceparent(row.Traceparent, out var context)
                ? context.TraceId.ToString()
                : null,
            EventDeliveries = deliveries.Select(delivery => new EventDeliveryDto
            {
                EventDeliveryId = delivery.EventDeliveryId,
                SubscriptionId = delivery.SubscriptionId,
                DestinationConnectionId = delivery.DestinationConnectionId,
                Status = delivery.Status,
                LifetimeAttemptCount = delivery.LifetimeAttemptCount,
                RetryCycleAttemptCount = delivery.RetryCycleAttemptCount,
                DeliverAfter = delivery.DeliverAfter,
                FailedAt = delivery.FailedAt
            }).ToList(),
            DeliveryAttempts = attempts.Select(attempt => new DeliveryAttemptDiagnosticsDto
            {
                AttemptId = attempt.AttemptId,
                EventDeliveryId = attempt.EventDeliveryId,
                SubscriptionId = attempt.SubscriptionId,
                DestinationConnectionId = attempt.DestinationConnectionId,
                AttemptNumber = attempt.AttemptNumber,
                Status = attempt.Status,
                FailurePhase = attempt.FailurePhase,
                ResponseStatusCode = attempt.ResponseStatusCode,
                ErrorMessage = attempt.ErrorMessage,
                StartedAt = attempt.StartedAt,
                CompletedAt = attempt.CompletedAt,
                RequestPayload = Parse(attempt.RequestPayloadJson),
                ResponseBody = attempt.ResponseBody,
                ResponseBodyTruncated = attempt.ResponseBodyTruncated
            }).ToList()
        };
    }

    // Stored JSON is returned as text and reparsed rather than passed through as a string, so the
    // Admin response carries it as JSON an Operator can read instead of an escaped blob.
    private static JsonElement? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record EventRow
    {
        public Guid Id { get; init; }
        public string Status { get; init; } = "";
        public string? EventType { get; init; }
        public DateTimeOffset AcceptedAt { get; init; }
        public DateTimeOffset? ProcessedAt { get; init; }
        public DateTimeOffset? FailedAt { get; init; }
        public string? PayloadJson { get; init; }
        public string? MetadataJson { get; init; }
        public string? Traceparent { get; init; }
    }

    private sealed record DeliveryRow
    {
        public Guid EventDeliveryId { get; init; }
        public Guid SubscriptionId { get; init; }
        public Guid DestinationConnectionId { get; init; }
        public string Status { get; init; } = "";
        public int LifetimeAttemptCount { get; init; }
        public int RetryCycleAttemptCount { get; init; }
        public DateTimeOffset? DeliverAfter { get; init; }
        public DateTimeOffset? FailedAt { get; init; }
    }

    private sealed record AttemptRow
    {
        public Guid AttemptId { get; init; }
        public Guid EventDeliveryId { get; init; }
        public Guid SubscriptionId { get; init; }
        public Guid DestinationConnectionId { get; init; }
        public int AttemptNumber { get; init; }
        public string Status { get; init; } = "";
        public string? FailurePhase { get; init; }
        public int? ResponseStatusCode { get; init; }
        public string? ErrorMessage { get; init; }
        public DateTimeOffset StartedAt { get; init; }
        public DateTimeOffset? CompletedAt { get; init; }
        public string? RequestPayloadJson { get; init; }
        public string? ResponseBody { get; init; }
        public bool ResponseBodyTruncated { get; init; }
    }
}
