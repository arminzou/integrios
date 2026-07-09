using System.Text.Json;
using Dapper;
using Integrios.Application.Abstractions;
using Integrios.Domain.Integrations;

namespace Integrios.Infrastructure.Data;

public sealed class SubscriptionDeliveryRepository(IDbConnectionFactory connectionFactory) : ISubscriptionDeliveryRepository
{
    public async Task<int> FanoutAsync(
        Guid eventId,
        IReadOnlyList<SubscriptionFanoutTarget> targets,
        string? traceparent = null,
        CancellationToken cancellationToken = default)
    {
        if (targets.Count == 0)
        {
            return 0;
        }

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        return await connection.ExecuteAsync(
            new CommandDefinition(
                """
                INSERT INTO subscription_deliveries (event_id, subscription_id, destination_connection_id, transform_config_snapshot, traceparent)
                VALUES (@EventId, @SubscriptionId, @DestinationConnectionId, @TransformConfigJson::jsonb, @Traceparent)
                ON CONFLICT (event_id, subscription_id) DO NOTHING;
                """,
                targets.Select(t => new
                {
                    EventId = eventId,
                    t.SubscriptionId,
                    t.DestinationConnectionId,
                    TransformConfigJson = t.TransformConfigJson,
                    Traceparent = traceparent
                }),
                cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<SubscriptionDeliveryWorkItem>> ClaimBatchAsync(
        int limit,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<DeliveryWorkRow>(
            new CommandDefinition(
                """
                WITH claimed AS (
                    SELECT id
                    FROM subscription_deliveries
                    WHERE status = 'pending'
                      AND (deliver_after IS NULL OR deliver_after <= now())
                    ORDER BY created_at, id
                    LIMIT @Limit
                    FOR UPDATE SKIP LOCKED
                ),
                updated AS (
                    UPDATE subscription_deliveries sd
                    SET status = 'in_flight',
                        updated_at = now()
                    WHERE sd.id IN (SELECT id FROM claimed)
                    RETURNING id, event_id, subscription_id, destination_connection_id, attempt_count, transform_config_snapshot, traceparent
                )
                SELECT
                    u.id AS Id,
                    u.event_id AS EventId,
                    u.subscription_id AS SubscriptionId,
                    u.destination_connection_id AS DestinationConnectionId,
                    c.tenant_id AS TenantId,
                    u.attempt_count AS AttemptCount,
                    c.config->>'url' AS DestinationUrl,
                    e.payload::text AS PayloadJson,
                    e.event_type AS EventType,
                    t.name AS TopicName,
                    e.accepted_at AS AcceptedAt,
                    u.transform_config_snapshot AS TransformConfigSnapshot,
                    i.key AS IntegrationKey,
                    c.auth::text AS DestinationAuthJson,
                    u.traceparent AS Traceparent
                FROM updated u
                JOIN events e ON e.id = u.event_id
                JOIN connections c ON c.id = u.destination_connection_id
                JOIN integrations i ON i.id = c.integration_id
                LEFT JOIN topics t ON t.id = e.topic_id
                ORDER BY e.accepted_at, u.id;
                """,
                new { Limit = limit },
                cancellationToken: cancellationToken));

        return rows.Select(r => new SubscriptionDeliveryWorkItem(
            r.Id,
            r.EventId,
            r.SubscriptionId,
            r.DestinationConnectionId,
            r.TenantId,
            r.AttemptCount,
            r.DestinationUrl ?? string.Empty,
            r.PayloadJson ?? string.Empty,
            r.EventType ?? string.Empty,
            r.TopicName,
            r.AcceptedAt,
            r.TransformConfigSnapshot,
            r.IntegrationKey ?? string.Empty,
            string.IsNullOrWhiteSpace(r.DestinationAuthJson) ? null : JsonSerializer.Deserialize<ConnectionAuth>(r.DestinationAuthJson),
            r.Traceparent)).ToList();
    }

    public async Task MarkSucceededAsync(Guid deliveryId, CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE subscription_deliveries
                SET status = 'succeeded',
                    processed_at = now(),
                    updated_at = now(),
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
                SET status = 'pending',
                    attempt_count = @NewAttemptCount,
                    deliver_after = @DeliverAfter,
                    updated_at = now()
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
                SET status = 'dead_lettered',
                    failed_at = now(),
                    updated_at = now(),
                    attempt_count = attempt_count + 1
                WHERE id = @Id
                """,
                new { Id = deliveryId },
                cancellationToken: cancellationToken));
    }

    private sealed record DeliveryWorkRow
    {
        public Guid Id { get; init; }
        public Guid EventId { get; init; }
        public Guid SubscriptionId { get; init; }
        public Guid DestinationConnectionId { get; init; }
        public Guid TenantId { get; init; }
        public int AttemptCount { get; init; }
        public string? DestinationUrl { get; init; }
        public string? PayloadJson { get; init; }
        public string? EventType { get; init; }
        public string? TopicName { get; init; }
        public DateTimeOffset AcceptedAt { get; init; }
        public string? TransformConfigSnapshot { get; init; }
        public string? IntegrationKey { get; init; }
        public string? DestinationAuthJson { get; init; }
        public string? Traceparent { get; init; }
    }
}
