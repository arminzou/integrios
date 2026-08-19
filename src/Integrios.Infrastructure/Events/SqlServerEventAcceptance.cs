using System.Text.Json;
using Dapper;
using Integrios.Application.Events;
using Integrios.Domain.Events;
using Integrios.Infrastructure.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using DomainEvent = Integrios.Domain.Events.Event;

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
                    @EventType, @PayloadJson, @MetadataJson, @IdempotencyKey, N'accepted', @AcceptedAt)
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
