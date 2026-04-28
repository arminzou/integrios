using Dapper;
using Integrios.Application.Abstractions;

namespace Integrios.Infrastructure.Data;

public sealed class OutboxRepository(IDbConnectionFactory connectionFactory) : IOutboxRepository
{
    public async Task<IReadOnlyList<OutboxRow>> ClaimBatchAsync(int limit, CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);

        var rows = await connection.QueryAsync<OutboxRow>(
            new CommandDefinition(
                """
                SELECT id AS Id, event_id AS EventId, attempt_count AS AttemptCount
                FROM outbox
                WHERE processed_at IS NULL
                  AND (deliver_after IS NULL OR deliver_after <= now())
                ORDER BY deliver_after NULLS FIRST, created_at ASC
                LIMIT @Limit
                FOR UPDATE SKIP LOCKED
                """,
                new { Limit = limit },
                cancellationToken: cancellationToken));

        return rows.ToList();
    }

    public async Task MarkProcessedAsync(Guid outboxId, CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);

        await connection.ExecuteAsync(
            new CommandDefinition(
                "UPDATE outbox SET processed_at = now() WHERE id = @Id",
                new { Id = outboxId },
                cancellationToken: cancellationToken));
    }

    public async Task<EventDetails?> GetEventAsync(Guid eventId, CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);

        return await connection.QuerySingleOrDefaultAsync<EventDetails>(
            new CommandDefinition(
                """
                SELECT
                    id            AS Id,
                    tenant_id     AS TenantId,
                    event_type    AS EventType,
                    payload::text AS PayloadJson,
                    topic_id      AS TopicId
                FROM events
                WHERE id = @EventId
                """,
                new { EventId = eventId },
                cancellationToken: cancellationToken));
    }

    public async Task ScheduleRetryAsync(
        Guid outboxId,
        int newAttemptCount,
        DateTimeOffset deliverAfter,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);

        await connection.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE outbox
                SET attempt_count = @NewAttemptCount,
                    deliver_after  = @DeliverAfter
                WHERE id = @Id
                """,
                new { Id = outboxId, NewAttemptCount = newAttemptCount, DeliverAfter = deliverAfter },
                cancellationToken: cancellationToken));
    }

    public async Task UpdateEventStatusAsync(
        Guid eventId,
        string status,
        Guid? topicId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);

        await connection.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE events
                SET
                    status       = @Status,
                    topic_id     = COALESCE(@TopicId, topic_id),
                    processed_at = CASE WHEN @Status = 'completed'     THEN now() ELSE processed_at END,
                    failed_at    = CASE WHEN @Status IN ('failed', 'dead_lettered') THEN now() ELSE failed_at END
                WHERE id = @EventId
                """,
                new { EventId = eventId, Status = status, TopicId = topicId },
                cancellationToken: cancellationToken));
    }
}
