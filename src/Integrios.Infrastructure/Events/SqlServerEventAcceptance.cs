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
    public async Task<EventAcceptance?> FindBySourceEventIdAsync(
        Guid sourceId,
        string sourceEventId,
        CancellationToken cancellationToken)
    {
        await using IntegriosDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = context.Database.GetDbConnection();
        EventAcceptanceRow? existing = await connection.QuerySingleOrDefaultAsync<EventAcceptanceRow>(new CommandDefinition(
            "SELECT id AS EventId, status AS Status, accepted_at AS AcceptedAt FROM events WHERE source_id=@SourceId AND source_event_id=@SourceEventId",
            new { SourceId = sourceId, SourceEventId = sourceEventId }, cancellationToken: cancellationToken));
        return existing is null ? null : new EventAcceptance
        {
            EventId = existing.EventId,
            Status = existing.Status,
            AcceptedAt = existing.AcceptedAt,
            AlreadyAccepted = true
        };
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

        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var connection = context.Database.GetDbConnection();
        var dbTransaction = transaction.GetDbTransaction();

        try
        {
            // HOLDLOCK keeps the shared lock on the Source and Tenant rows until commit, so a
            // Source disable, declaration change, or Tenant deactivation waits for acceptances
            // already past this point and every later one sees it.
            string? declared = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
                "SELECT s.event_types FROM sources s WITH (HOLDLOCK, ROWLOCK) JOIN tenants t WITH (HOLDLOCK, ROWLOCK) ON t.id = s.tenant_id WHERE s.tenant_id=@TenantId AND s.id=@SourceId AND s.topic_id=@TopicId AND s.status=N'active' AND s.deleted_at IS NULL AND t.status=N'active'",
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

            return ToAlreadyAccepted(existing);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private static bool IsIdempotencyConflict(SqlException ex, string? idempotencyKey) =>
        !string.IsNullOrWhiteSpace(idempotencyKey) && ex.Number is 2601 or 2627;

    private static EventAcceptance ToAlreadyAccepted(DomainEvent existing) => new()
    {
        EventId = existing.Id,
        Status = existing.Status,
        AcceptedAt = existing.AcceptedAt,
        AlreadyAccepted = true
    };

    private sealed record EventAcceptanceRow(Guid EventId, EventStatus Status, DateTimeOffset AcceptedAt);
}
