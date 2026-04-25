using System.Text.Json;
using Dapper;
using Integrios.Application.Abstractions;
using Integrios.Domain.Contracts;
using Integrios.Domain.Events;
using Npgsql;

namespace Integrios.Infrastructure.Data;

public sealed class EventRepository(IDbConnectionFactory connectionFactory) : IEventRepository
{
    public async Task<IngestEventResponse> IngestAsync(
        Guid tenantId,
        IngestEventRequest request,
        CancellationToken cancellationToken = default)
    {
        var eventId = Guid.NewGuid();
        var acceptedAt = DateTimeOffset.UtcNow;
        var payloadJson = JsonSerializer.Serialize(request.Payload);
        var metadataJson = request.Metadata is { } metadata ? JsonSerializer.Serialize(metadata) : null;
        var outboxPayloadJson = JsonSerializer.Serialize(new
        {
            eventId,
            tenantId,
            request.EventType,
            request.SourceEventId,
            request.IdempotencyKey,
            request.Payload,
            request.Metadata,
            acceptedAt
        });

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            const string insertEventSql = """
                INSERT INTO events (
                    id,
                    tenant_id,
                    source_event_id,
                    event_type,
                    payload,
                    metadata,
                    idempotency_key,
                    status,
                    accepted_at
                )
                VALUES (
                    @EventId,
                    @TenantId,
                    @SourceEventId,
                    @EventType,
                    CAST(@PayloadJson AS jsonb),
                    CAST(@MetadataJson AS jsonb),
                    @IdempotencyKey,
                    'accepted',
                    @AcceptedAt
                );
                """;

            await connection.ExecuteAsync(
                new CommandDefinition(
                    insertEventSql,
                    new
                    {
                        EventId = eventId,
                        TenantId = tenantId,
                        request.SourceEventId,
                        request.EventType,
                        PayloadJson = payloadJson,
                        MetadataJson = metadataJson,
                        request.IdempotencyKey,
                        AcceptedAt = acceptedAt
                    },
                    transaction,
                    cancellationToken: cancellationToken));

            const string insertOutboxSql = """
                INSERT INTO outbox (event_id, payload)
                VALUES (@EventId, CAST(@PayloadJson AS jsonb));
                """;

            await connection.ExecuteAsync(
                new CommandDefinition(
                    insertOutboxSql,
                    new
                    {
                        EventId = eventId,
                        PayloadJson = outboxPayloadJson
                    },
                    transaction,
                    cancellationToken: cancellationToken));

            await transaction.CommitAsync(cancellationToken);

            return new IngestEventResponse
            {
                EventId = eventId,
                Status = EventStatus.Accepted,
                AcceptedAt = acceptedAt,
                IsDuplicate = false
            };
        }
        catch (PostgresException ex) when (IsIdempotencyConflict(ex, request.IdempotencyKey))
        {
            await transaction.RollbackAsync(cancellationToken);

            var existing = await connection.QuerySingleOrDefaultAsync<ExistingEventRow>(
                new CommandDefinition(
                    """
                    SELECT id, status, accepted_at
                    FROM events
                    WHERE tenant_id = @TenantId
                      AND idempotency_key = @IdempotencyKey
                    LIMIT 1;
                    """,
                    new { TenantId = tenantId, request.IdempotencyKey },
                    cancellationToken: cancellationToken));

            if (existing is null)
                throw;

            return new IngestEventResponse
            {
                EventId = existing.Id,
                Status = ParseStatus(existing.Status),
                AcceptedAt = existing.AcceptedAt,
                IsDuplicate = true
            };
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private static bool IsIdempotencyConflict(PostgresException ex, string? idempotencyKey)
    {
        return !string.IsNullOrWhiteSpace(idempotencyKey)
               && ex.SqlState == PostgresErrorCodes.UniqueViolation
               && string.Equals(ex.ConstraintName, "idx_events_idempotency", StringComparison.Ordinal);
    }

    private static EventStatus ParseStatus(string status)
    {
        if (string.Equals(status, "dead_lettered", StringComparison.OrdinalIgnoreCase))
            return EventStatus.DeadLettered;
        return Enum.TryParse<EventStatus>(status, ignoreCase: true, out var parsed)
            ? parsed
            : EventStatus.Accepted;
    }

    private sealed record ExistingEventRow
    {
        public Guid Id { get; init; }
        public string Status { get; init; } = "";
        public DateTimeOffset AcceptedAt { get; init; }
    }

    public async Task<GetEventResponse?> GetEventByIdAsync(
        Guid tenantId,
        Guid eventId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);

        var row = await connection.QuerySingleOrDefaultAsync<EventByIdRow>(
            new CommandDefinition(
                """
                SELECT
                    id           AS Id,
                    status       AS Status,
                    accepted_at  AS AcceptedAt,
                    processed_at AS ProcessedAt,
                    failed_at    AS FailedAt
                FROM events
                WHERE tenant_id = @TenantId
                  AND id = @EventId
                LIMIT 1;
                """,
                new { TenantId = tenantId, EventId = eventId },
                cancellationToken: cancellationToken));

        if (row is null)
            return null;

        var attempts = await connection.QueryAsync<DeliveryAttemptRow>(
            new CommandDefinition(
                """
                SELECT
                    route_id                  AS RouteId,
                    destination_connection_id AS DestinationConnectionId,
                    attempt_number            AS AttemptNumber,
                    status                    AS Status,
                    response_status_code      AS ResponseStatusCode,
                    error_message             AS ErrorMessage,
                    started_at                AS StartedAt,
                    completed_at              AS CompletedAt
                FROM delivery_attempts
                WHERE event_id = @EventId
                ORDER BY attempt_number, started_at;
                """,
                new { EventId = eventId },
                cancellationToken: cancellationToken));

        return new GetEventResponse
        {
            EventId = row.Id,
            Status = ParseStatus(row.Status),
            AcceptedAt = row.AcceptedAt,
            ProcessedAt = row.ProcessedAt,
            FailedAt = row.FailedAt,
            DeliveryAttempts = attempts.Select(a => new DeliveryAttemptSummary
            {
                RouteId = a.RouteId,
                DestinationConnectionId = a.DestinationConnectionId,
                AttemptNumber = a.AttemptNumber,
                Status = a.Status,
                ResponseStatusCode = a.ResponseStatusCode,
                ErrorMessage = a.ErrorMessage,
                StartedAt = a.StartedAt,
                CompletedAt = a.CompletedAt
            }).ToList()
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

    public async Task<bool> ReplayEventAsync(
        Guid tenantId,
        Guid eventId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            var row = await connection.QuerySingleOrDefaultAsync<ReplayableEventRow>(
                new CommandDefinition(
                    """
                    SELECT e.id AS Id, e.status AS Status, o.payload::text AS OutboxPayloadJson
                    FROM events e
                    JOIN outbox o ON o.event_id = e.id
                    WHERE e.tenant_id = @TenantId
                      AND e.id = @EventId
                      AND e.status IN ('failed', 'dead_lettered')
                    ORDER BY o.created_at DESC
                    LIMIT 1
                    FOR UPDATE OF e
                    """,
                    new { TenantId = tenantId, EventId = eventId },
                    transaction,
                    cancellationToken: cancellationToken));

            if (row is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return false;
            }

            await connection.ExecuteAsync(
                new CommandDefinition(
                    """
                    UPDATE events
                    SET status       = 'accepted',
                        processed_at = NULL,
                        failed_at    = NULL
                    WHERE id = @EventId
                    """,
                    new { EventId = eventId },
                    transaction,
                    cancellationToken: cancellationToken));

            await connection.ExecuteAsync(
                new CommandDefinition(
                    """
                    INSERT INTO outbox (event_id, payload)
                    VALUES (@EventId, @OutboxPayloadJson::jsonb)
                    """,
                    new { EventId = eventId, row.OutboxPayloadJson },
                    transaction,
                    cancellationToken: cancellationToken));

            await transaction.CommitAsync(cancellationToken);
            return true;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private sealed record ReplayableEventRow
    {
        public Guid Id { get; init; }
        public string Status { get; init; } = "";
        public string OutboxPayloadJson { get; init; } = "";
    }

    private sealed record DeliveryAttemptRow
    {
        public Guid RouteId { get; init; }
        public Guid DestinationConnectionId { get; init; }
        public int AttemptNumber { get; init; }
        public string Status { get; init; } = "";
        public int? ResponseStatusCode { get; init; }
        public string? ErrorMessage { get; init; }
        public DateTimeOffset StartedAt { get; init; }
        public DateTimeOffset? CompletedAt { get; init; }
    }
}
