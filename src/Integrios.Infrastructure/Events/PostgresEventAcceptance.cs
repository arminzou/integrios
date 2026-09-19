using System.Text.Json;
using Dapper;
using Integrios.Application.Ingestion;
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
    public async Task<EventAcceptance?> FindBySourceEventIdAsync(
        Guid sourceId,
        string sourceEventId,
        CancellationToken cancellationToken)
    {
        await using IntegriosDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);
        DomainEvent? existing = await context.Events.AsNoTracking().SingleOrDefaultAsync(
            candidate => candidate.SourceId == sourceId && candidate.SourceEventId == sourceEventId,
            cancellationToken);
        return existing is null ? null : ToAlreadyAccepted(existing);
    }

    public async Task<EventAcceptance> AcceptAsync(
        EventSubmission submission,
        string? traceparent,
        CancellationToken cancellationToken)
    {
        await using IntegriosDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);
        // The retrying execution strategy refuses a user-initiated transaction unless the whole
        // transaction is the retried unit. The context stays outside: every read here is untracked.
        return await context.Database.CreateExecutionStrategy().ExecuteAsync(
            ct => AcceptInTransactionAsync(context, submission, traceparent, ct),
            cancellationToken);
    }

    private static async Task<EventAcceptance> AcceptInTransactionAsync(
        IntegriosDbContext context,
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
            submission.SourceId,
            submission.IdempotencyKey,
            submission.Payload,
            submission.Metadata,
            acceptedAt
        });

        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var connection = context.Database.GetDbConnection();
        var dbTransaction = transaction.GetDbTransaction();

        try
        {
            // FOR SHARE holds the Source row until commit, so a disable or declaration change
            // waits for acceptances already past this point and every later one sees it.
            string? declared = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
                "SELECT event_types::text FROM sources WHERE tenant_id=@TenantId AND id=@SourceId AND topic_id=@TopicId AND status='enabled' AND deleted_at IS NULL FOR SHARE",
                new { submission.TenantId, submission.SourceId, submission.TopicId }, dbTransaction, cancellationToken: cancellationToken));
            SourceAuthority.Ensure(
                declared is null ? null : JsonSerializer.Deserialize<string[]>(declared), submission.EventType);

            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO events (
                    id, tenant_id, topic_id, source_id, source_event_id,
                    event_type, payload, metadata, idempotency_key, status, accepted_at)
                VALUES (
                    @EventId, @TenantId, @TopicId, @SourceId, @SourceEventId,
                    @EventType, CAST(@PayloadJson AS jsonb), CAST(@MetadataJson AS jsonb),
                    @IdempotencyKey, 'accepted', @AcceptedAt)
                """,
                new
                {
                    EventId = eventId,
                    submission.TenantId,
                    submission.TopicId,
                    submission.SourceId,
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

            return ToAlreadyAccepted(existing);
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

    private static EventAcceptance ToAlreadyAccepted(DomainEvent existing) => new()
    {
        EventId = existing.Id,
        Status = existing.Status,
        AcceptedAt = existing.AcceptedAt,
        AlreadyAccepted = true
    };
}
