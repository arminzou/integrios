using Dapper;
using Integrios.Application.Delivery;
using Integrios.Infrastructure.Data;

namespace Integrios.Infrastructure.Delivery;

internal sealed class DeadLetterReplay(IDbConnectionFactory connectionFactory) : IDeadLetterReplay
{
    public async Task<DeadLetterReplayResult> ReplayAsync(
        Guid tenantId,
        Guid eventId,
        Guid subscriptionDeliveryId,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        bool sqlServer = connectionFactory.Provider == DatabaseProvider.SqlServer;

        string? status = await connection.QuerySingleOrDefaultAsync<string>(
            new CommandDefinition(
                """
                SELECT sd.status
                FROM event_deliveries sd
                JOIN events e ON e.id = sd.event_id
                WHERE e.tenant_id = @TenantId
                  AND e.id = @EventId
                  AND sd.id = @EventDeliveryId;
                """,
                new { TenantId = tenantId, EventId = eventId, EventDeliveryId = subscriptionDeliveryId },
                cancellationToken: cancellationToken));

        if (status is null)
            return DeadLetterReplayResult.NotFound;
        if (!string.Equals(status, "dead_lettered", StringComparison.Ordinal))
            return DeadLetterReplayResult.NotDeadLettered;

        int resetCount = await connection.ExecuteAsync(
            new CommandDefinition(
                sqlServer
                ? """
                UPDATE sd
                SET status = N'pending', retry_cycle_attempt_count = 0, deliver_after = NULL,
                    active_attempt_id = NULL, lease_expires_at = NULL, failed_at = NULL,
                    updated_at = SYSUTCDATETIME()
                FROM event_deliveries sd
                JOIN events e ON e.id = sd.event_id
                WHERE e.tenant_id = @TenantId AND e.id = @EventId
                  AND sd.id = @EventDeliveryId AND sd.status = N'dead_lettered';
                """
                : """
                UPDATE event_deliveries sd
                SET status = 'pending',
                    retry_cycle_attempt_count = 0,
                    deliver_after = NULL,
                    active_attempt_id = NULL,
                    lease_expires_at = NULL,
                    failed_at = NULL,
                    updated_at = now()
                FROM events e
                WHERE sd.event_id = e.id
                  AND e.tenant_id = @TenantId
                  AND e.id = @EventId
                  AND sd.id = @EventDeliveryId
                  AND sd.status = 'dead_lettered';
                """,
                new { TenantId = tenantId, EventId = eventId, EventDeliveryId = subscriptionDeliveryId },
                cancellationToken: cancellationToken));

        return resetCount > 0 ? DeadLetterReplayResult.Replayed : DeadLetterReplayResult.NotDeadLettered;
    }
}
