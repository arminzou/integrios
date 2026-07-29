using Dapper;
using Integrios.Application.Delivery;
using Integrios.Infrastructure.Data;

namespace Integrios.Infrastructure.Transport;

public sealed class PostgresDeadLetterReplay(IDbConnectionFactory connectionFactory) : IDeadLetterReplay
{
    public async Task<bool> ReplayDeadLetteredAsync(
        Guid tenantId,
        Guid eventId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);

        int resetCount = await connection.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE subscription_deliveries sd
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
                  AND sd.status = 'dead_lettered';
                """,
                new { TenantId = tenantId, EventId = eventId },
                cancellationToken: cancellationToken));

        return resetCount > 0;
    }
}
