using System.Text.Json;
using Dapper;
using Integrios.Application.Ingestion;
using Integrios.Domain.Entities;
using Integrios.Domain.Enums;
using Integrios.Infrastructure.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using DomainEvent = Integrios.Domain.Entities.Event;

namespace Integrios.Infrastructure.Events;

internal sealed class SqlServerEventAcceptance(IDbContextFactory<IntegriosDbContext> contextFactory)
    : IEventAcceptance
{
    public async Task<EventAcceptance> AcceptAsync(
        EventSubmission submission,
        string? traceparent,
        CancellationToken cancellationToken)
    {
        var eventId = Guid.NewGuid();
        var acceptedAt = DateTimeOffset.UtcNow;
        string payloadJson = JsonSerializer.Serialize(submission.Payload);
        string? metadataJson = submission.Metadata is { } metadata ? JsonSerializer.Serialize(metadata) : null;
        string outboxPayloadJson = JsonSerializer.Serialize(new
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

        await using IntegriosDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var connection = context.Database.GetDbConnection();
        var dbTransaction = transaction.GetDbTransaction();

        try
        {
            bool activeSource = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
                "SELECT CAST(CASE WHEN EXISTS (SELECT 1 FROM sources WHERE tenant_id=@TenantId AND id=@SourceId AND topic_id=@TopicId AND status=N'active') THEN 1 ELSE 0 END AS bit)",
                new { submission.TenantId, submission.SourceId, submission.TopicId }, dbTransaction, cancellationToken: cancellationToken));
            if (!activeSource)
                throw new EventAcceptanceException("The Source is not active for the requested Topic.");

            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO events (
                    id, tenant_id, topic_id, source_id, source_event_id,
                    event_type, payload, metadata, idempotency_key, status, accepted_at)
                VALUES (
                    @EventId, @TenantId, @TopicId, @SourceId, @SourceEventId,
                    @EventType, @PayloadJson, @MetadataJson, @IdempotencyKey, N'accepted', @AcceptedAt)
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
                "INSERT INTO outbox (event_id, payload, traceparent) VALUES (@EventId, @PayloadJson, @Traceparent)",
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
        catch (SqlException ex) when (IsIdempotencyConflict(ex, submission.IdempotencyKey))
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
        catch (SqlException ex) when (ex.Number == 51001)
        {
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

    private static bool IsIdempotencyConflict(SqlException ex, string? idempotencyKey) =>
        !string.IsNullOrWhiteSpace(idempotencyKey) && ex.Number is 2601 or 2627;
}
