using System.Text.Json;
using Dapper;
using Integrios.Application.Events;
using Integrios.Domain.Entities;
using Integrios.Domain.Enums;
using Integrios.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using DomainEvent = Integrios.Domain.Entities.Event;

namespace Integrios.Infrastructure.Events;

internal sealed class PostgresEventAcceptance(IDbContextFactory<IntegriosDbContext> contextFactory)
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

        await using IntegriosDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var connection = context.Database.GetDbConnection();
        var dbTransaction = transaction.GetDbTransaction();

        try
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO events (
                    id, tenant_id, topic_id, source_connection_id, source_event_id,
                    event_type, payload, metadata, idempotency_key, status, accepted_at)
                VALUES (
                    @EventId, @TenantId, @TopicId, @SourceConnectionId, @SourceEventId,
                    @EventType, CAST(@PayloadJson AS jsonb), CAST(@MetadataJson AS jsonb),
                    @IdempotencyKey, 'accepted', @AcceptedAt)
                """,
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
                    AcceptedAt = acceptedAt,
                },
                dbTransaction,
                cancellationToken: cancellationToken));

            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO outbox (event_id, payload, traceparent)
                VALUES (@EventId, CAST(@PayloadJson AS jsonb), @Traceparent)
                """,
                new { EventId = eventId, PayloadJson = outboxPayloadJson, Traceparent = traceparent },
                dbTransaction,
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

            DomainEvent? existing = await context.Events.AsNoTracking().SingleOrDefaultAsync(
                candidate => candidate.TenantId == submission.TenantId
                    && candidate.IdempotencyKey == submission.IdempotencyKey,
                cancellationToken);

            if (existing is null)
                throw;

            return new EventAcceptance
            {
                EventId = existing.Id,
                Status = existing.Status,
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
}
