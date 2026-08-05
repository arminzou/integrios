using System.Text.Json;
using Dapper;
using Integrios.Application.Events;
using Integrios.Domain.Events;
using Integrios.Infrastructure.Data;
using Npgsql;

namespace Integrios.Infrastructure.Events;

internal sealed class PostgresEventAcceptance(IDbConnectionFactory connectionFactory)
    : IEventAcceptance
{
    public async Task<EventAcceptance> AcceptAsync(
        EventSubmission submission,
        string? traceparent,
        CancellationToken cancellationToken)
    {
        var eventId = Guid.NewGuid();
        var acceptedAt = DateTimeOffset.UtcNow;
        var payloadJson = JsonSerializer.Serialize(submission.Payload);
        var metadataJson = submission.Metadata is { } metadata ? JsonSerializer.Serialize(metadata) : null;
        var outboxPayloadJson = JsonSerializer.Serialize(new
        {
            eventId,
            tenantId = submission.TenantId,
            submission.EventType,
            submission.SourceEventId,
            submission.SourceConnectionId,
            submission.IdempotencyKey,
            submission.Payload,
            submission.Metadata,
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
                    topic_id,
                    source_connection_id,
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
                    @TopicId,
                    @SourceConnectionId,
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
                        submission.TenantId,
                        submission.TopicId,
                        submission.SourceConnectionId,
                        submission.SourceEventId,
                        submission.EventType,
                        PayloadJson = payloadJson,
                        MetadataJson = metadataJson,
                        submission.IdempotencyKey,
                        AcceptedAt = acceptedAt
                    },
                    transaction,
                    cancellationToken: cancellationToken));

            const string insertOutboxSql = """
                INSERT INTO outbox (event_id, payload, traceparent)
                VALUES (@EventId, CAST(@PayloadJson AS jsonb), @Traceparent);
                """;

            await connection.ExecuteAsync(
                new CommandDefinition(
                    insertOutboxSql,
                    new
                    {
                        EventId = eventId,
                        PayloadJson = outboxPayloadJson,
                        Traceparent = traceparent
                    },
                    transaction,
                    cancellationToken: cancellationToken));

            await transaction.CommitAsync(cancellationToken);

            return new EventAcceptance
            {
                EventId = eventId,
                Status = EventStatus.Accepted,
                AcceptedAt = acceptedAt,
                AlreadyAccepted = false
            };
        }
        catch (PostgresException ex) when (IsIdempotencyConflict(ex, submission.IdempotencyKey))
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
                    new { submission.TenantId, submission.IdempotencyKey },
                    cancellationToken: cancellationToken));

            if (existing is null)
                throw;

            return new EventAcceptance
            {
                EventId = existing.Id,
                Status = EventStatusMap.FromDbValue(existing.Status),
                AcceptedAt = existing.AcceptedAt,
                AlreadyAccepted = true
            };
        }
        catch (PostgresException ex) when (IsRetiredTopicSource(ex))
        {
            // The source was retired between resolving the Topic and this insert. The trigger is
            // the authority, so a lost race answers the producer the same way the read path does.
            await transaction.RollbackAsync(cancellationToken);
            throw new EventAcceptanceException(
                "The source Connection is not actively associated with the requested Topic.");
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private static bool IsRetiredTopicSource(PostgresException ex) =>
        ex.SqlState == PostgresErrorCodes.ForeignKeyViolation
        && string.Equals(ex.ConstraintName, "fk_events_topic_source_active", StringComparison.Ordinal);

    private static bool IsIdempotencyConflict(PostgresException ex, string? idempotencyKey)
    {
        return !string.IsNullOrWhiteSpace(idempotencyKey)
               && ex.SqlState == PostgresErrorCodes.UniqueViolation
               && string.Equals(ex.ConstraintName, "idx_events_idempotency", StringComparison.Ordinal);
    }

    private sealed record ExistingEventRow
    {
        public Guid Id { get; init; }
        public string Status { get; init; } = "";
        public DateTimeOffset AcceptedAt { get; init; }
    }
}
