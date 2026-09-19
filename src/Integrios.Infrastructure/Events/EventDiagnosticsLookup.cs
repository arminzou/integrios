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
        string sourceDeleted = Deleted("s", sqlServer);
        string topicDeleted = Deleted("t", sqlServer);
        string topicKey = sqlServer ? "t.[key]" : "t.key";

        var row = await connection.QuerySingleOrDefaultAsync<EventRow>(
            new CommandDefinition(
                $"""
                SELECT
                    {top}events.id         AS Id,
                    events.status          AS Status,
                    events.event_type      AS EventType,
                    events.source_id AS SourceId,
                    s.name           AS SourceName,
                    {sourceDeleted}  AS SourceDeleted,
                    events.topic_id  AS TopicId,
                    {topicKey}       AS TopicKey,
                    t.name           AS TopicName,
                    {topicDeleted}   AS TopicDeleted,
                    events.accepted_at     AS AcceptedAt,
                    events.processed_at    AS ProcessedAt,
                    events.failed_at       AS FailedAt,
                    events.{payload}       AS PayloadJson,
                    events.{metadata}      AS MetadataJson,
                    (SELECT traceparent FROM outbox WHERE event_id = events.id) AS Traceparent
                FROM events
                LEFT JOIN sources s ON s.tenant_id = events.tenant_id AND s.id = events.source_id
                LEFT JOIN topics t ON t.tenant_id = events.tenant_id AND t.id = events.topic_id
                WHERE events.tenant_id = @TenantId
                  AND events.id = @EventId
                {limit}
                """,
                new { TenantId = tenantId, EventId = eventId },
                cancellationToken: cancellationToken));

        if (row is null)
            return null;

        string subscriptionDeleted = Deleted("s", sqlServer);
        string destinationDeleted = Deleted("d", sqlServer);
        var deliveries = await connection.QueryAsync<DeliveryRow>(
            new CommandDefinition(
                $"""
                SELECT
                    ed.id                        AS EventDeliveryId,
                    ed.subscription_id           AS SubscriptionId,
                    s.name                       AS SubscriptionName,
                    {subscriptionDeleted}        AS SubscriptionDeleted,
                    ed.destination_id            AS DestinationId,
                    d.name                       AS DestinationName,
                    {destinationDeleted}         AS DestinationDeleted,
                    ed.status                    AS Status,
                    ed.lifetime_attempt_count    AS LifetimeAttemptCount,
                    ed.retry_cycle_attempt_count AS RetryCycleAttemptCount,
                    ed.deliver_after             AS DeliverAfter,
                    ed.failed_at                 AS FailedAt
                FROM event_deliveries ed
                LEFT JOIN subscriptions s ON s.id = ed.subscription_id
                LEFT JOIN destinations d ON d.id = ed.destination_id
                WHERE ed.event_id = @EventId
                ORDER BY ed.id;
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
                    sd.destination_id            AS DestinationId,
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
            SourceId = row.SourceId,
            SourceName = row.SourceName,
            SourceDeleted = row.SourceDeleted,
            TopicId = row.TopicId,
            TopicKey = row.TopicKey,
            TopicName = row.TopicName,
            TopicDeleted = row.TopicDeleted,
            AcceptedAt = row.AcceptedAt,
            ProcessedAt = row.ProcessedAt,
            FailedAt = row.FailedAt,
            Payload = Parse(row.PayloadJson),
            Metadata = Parse(row.MetadataJson),
            TraceId = ActivitySources.TryParseTraceparent(row.Traceparent, out var context)
                ? context.TraceId.ToString()
                : null,
            EventDeliveries = deliveries.Select(delivery => new EventDeliveryDiagnosticsDto
            {
                EventDeliveryId = delivery.EventDeliveryId,
                SubscriptionId = delivery.SubscriptionId,
                SubscriptionName = delivery.SubscriptionName,
                SubscriptionDeleted = delivery.SubscriptionDeleted,
                DestinationId = delivery.DestinationId,
                DestinationName = delivery.DestinationName,
                DestinationDeleted = delivery.DestinationDeleted,
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
                DestinationId = attempt.DestinationId,
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
    // SQL Server has no boolean expression to select, so the flag is cast from a CASE.
    private static string Deleted(string alias, bool sqlServer) => sqlServer
        ? $"CAST(CASE WHEN {alias}.deleted_at IS NULL THEN 0 ELSE 1 END AS bit)"
        : $"({alias}.deleted_at IS NOT NULL)";

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
        public Guid? SourceId { get; init; }
        public string? SourceName { get; init; }
        public bool SourceDeleted { get; init; }
        public Guid? TopicId { get; init; }
        public string? TopicKey { get; init; }
        public string? TopicName { get; init; }
        public bool TopicDeleted { get; init; }
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
        public string? SubscriptionName { get; init; }
        public bool SubscriptionDeleted { get; init; }
        public Guid DestinationId { get; init; }
        public string? DestinationName { get; init; }
        public bool DestinationDeleted { get; init; }
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
        public Guid DestinationId { get; init; }
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
