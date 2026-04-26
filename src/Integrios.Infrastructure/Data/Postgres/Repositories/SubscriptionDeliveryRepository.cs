using Dapper;
using Integrios.Application.Abstractions;

namespace Integrios.Infrastructure.Data;

public sealed class SubscriptionDeliveryRepository(IDbConnectionFactory connectionFactory) : ISubscriptionDeliveryRepository
{
    public async Task<int> FanoutAsync(
        Guid eventId,
        IReadOnlyList<SubscriptionFanoutTarget> targets,
        CancellationToken cancellationToken = default)
    {
        if (targets.Count == 0) return 0;

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);

        return await connection.ExecuteAsync(
            new CommandDefinition(
                """
                INSERT INTO subscription_deliveries (event_id, subscription_id, destination_connection_id)
                VALUES (@EventId, @SubscriptionId, @DestinationConnectionId)
                ON CONFLICT (event_id, subscription_id) DO NOTHING
                """,
                targets.Select(t => new
                {
                    EventId = eventId,
                    t.SubscriptionId,
                    t.DestinationConnectionId
                }),
                cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<SubscriptionDeliveryWorkItem>> ClaimBatchAsync(
        int limit,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);

        var rows = await connection.QueryAsync<SubscriptionDeliveryWorkItem>(
            new CommandDefinition(
                """
                WITH claimed AS (
                    SELECT id
                    FROM subscription_deliveries
                    WHERE status = 'pending'
                      AND (deliver_after IS NULL OR deliver_after <= now())
                    ORDER BY deliver_after NULLS FIRST, created_at ASC
                    LIMIT @Limit
                    FOR UPDATE SKIP LOCKED
                ),
                updated AS (
                    UPDATE subscription_deliveries
                    SET status = 'in_flight', updated_at = now()
                    WHERE id IN (SELECT id FROM claimed)
                    RETURNING id, event_id, subscription_id, destination_connection_id, attempt_count
                )
                SELECT
                    u.id                        AS Id,
                    u.event_id                  AS EventId,
                    u.subscription_id           AS SubscriptionId,
                    u.destination_connection_id AS DestinationConnectionId,
                    u.attempt_count             AS AttemptCount,
                    c.config->>'url'            AS DestinationUrl,
                    e.payload::text             AS PayloadJson
                FROM updated u
                JOIN events      e ON e.id = u.event_id
                JOIN connections c ON c.id = u.destination_connection_id
                """,
                new { Limit = limit },
                cancellationToken: cancellationToken));

        return rows.ToList();
    }

    public async Task MarkSucceededAsync(Guid deliveryId, CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);

        await connection.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE subscription_deliveries
                SET status        = 'succeeded',
                    processed_at  = now(),
                    updated_at    = now(),
                    attempt_count = attempt_count + 1
                WHERE id = @Id
                """,
                new { Id = deliveryId },
                cancellationToken: cancellationToken));
    }

    public async Task ScheduleRetryAsync(
        Guid deliveryId,
        int newAttemptCount,
        DateTimeOffset deliverAfter,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);

        await connection.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE subscription_deliveries
                SET status        = 'pending',
                    attempt_count = @NewAttemptCount,
                    deliver_after = @DeliverAfter,
                    updated_at    = now()
                WHERE id = @Id
                """,
                new { Id = deliveryId, NewAttemptCount = newAttemptCount, DeliverAfter = deliverAfter },
                cancellationToken: cancellationToken));
    }

    public async Task MarkDeadLetteredAsync(Guid deliveryId, CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);

        await connection.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE subscription_deliveries
                SET status        = 'dead_lettered',
                    failed_at     = now(),
                    updated_at    = now(),
                    attempt_count = attempt_count + 1
                WHERE id = @Id
                """,
                new { Id = deliveryId },
                cancellationToken: cancellationToken));
    }
}
