using Dapper;
using Integrios.Application.Events;
using Integrios.Domain.Entities;
using Integrios.Domain.Enums;
using Integrios.Infrastructure.Data;

namespace Integrios.Infrastructure.Events;

internal sealed class TenantEventLookup(IDbConnectionFactory connectionFactory)
    : ITenantEventLookup
{
    public async Task<EventDto?> GetByIdAsync(
        Guid tenantId,
        Guid eventId,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        bool sqlServer = connectionFactory.Provider == DatabaseProvider.SqlServer;
        string top = sqlServer ? "TOP (1) " : string.Empty;
        string limit = sqlServer ? string.Empty : "LIMIT 1;";

        var row = await connection.QuerySingleOrDefaultAsync<EventByIdRow>(
            new CommandDefinition(
                $"""
                SELECT
                    {top}id      AS Id,
                    status       AS Status,
                    accepted_at  AS AcceptedAt,
                    processed_at AS ProcessedAt,
                    failed_at    AS FailedAt
                FROM events
                WHERE tenant_id = @TenantId
                  AND id = @EventId
                {limit}
                """,
                new { TenantId = tenantId, EventId = eventId },
                cancellationToken: cancellationToken));

        if (row is null)
            return null;

        var deliveries = await connection.QueryAsync<EventDeliveryRow>(
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

        var attempts = await connection.QueryAsync<DeliveryAttemptRow>(
            new CommandDefinition(
                """
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
                    da.completed_at              AS CompletedAt
                FROM delivery_attempts da
                JOIN event_deliveries sd ON sd.id = da.event_delivery_id
                WHERE sd.event_id = @EventId
                ORDER BY sd.id, da.attempt_number, da.started_at;
                """,
                new { EventId = eventId },
                cancellationToken: cancellationToken));

        var attemptDtos = attempts.Select(a => new DeliveryAttemptDto
        {
            AttemptId = a.AttemptId,
            EventDeliveryId = a.EventDeliveryId,
            SubscriptionId = a.SubscriptionId,
            DestinationConnectionId = a.DestinationConnectionId,
            AttemptNumber = a.AttemptNumber,
            Status = a.Status,
            FailurePhase = a.FailurePhase,
            ResponseStatusCode = a.ResponseStatusCode,
            ErrorMessage = a.ErrorMessage,
            StartedAt = a.StartedAt,
            CompletedAt = a.CompletedAt
        }).ToList();
        return new EventDto
        {
            EventId = row.Id,
            Status = EventStatusMap.FromDbValue(row.Status),
            AcceptedAt = row.AcceptedAt,
            ProcessedAt = row.ProcessedAt,
            FailedAt = row.FailedAt,
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
            DeliveryAttempts = attemptDtos
        };
    }

    private sealed record EventByIdRow
    {
        public Guid Id { get; init; }
        public string Status { get; init; } = "";
        public DateTimeOffset AcceptedAt { get; init; }
        public DateTimeOffset? ProcessedAt { get; init; }
        public DateTimeOffset? FailedAt { get; init; }
    }

    private sealed record DeliveryAttemptRow
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
    }

    private sealed record EventDeliveryRow
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
}
