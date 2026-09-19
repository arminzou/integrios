using Dapper;
using Integrios.Application.Ingestion;
using Integrios.Infrastructure.Data;

namespace Integrios.Infrastructure.Events;

internal sealed class TenantEventMonitoring(IDbConnectionFactory connectionFactory) : ITenantEventMonitoring
{
    public async Task<EventBacklogDto> GetBacklogAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        string sql = BacklogSql(connectionFactory.Provider == DatabaseProvider.SqlServer);
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        BacklogRow row = await connection.QuerySingleAsync<BacklogRow>(
            new CommandDefinition(sql, new { TenantId = tenantId }, cancellationToken: cancellationToken));
        return new EventBacklogDto(
            new EventBacklogItemDto(row.AwaitingCount, row.AwaitingOldestAt),
            new EventBacklogItemDto(row.UnroutedCount, row.UnroutedOldestAt),
            new EventBacklogItemDto(row.DeadLetteredCount, row.DeadLetteredOldestAt));
    }

    internal static string BacklogSql(bool sqlServer)
    {
        // One statement, one derived row per backlog, so the three values describe the same moment.
        // Each narrows by Tenant and current status before anything else; the unrouted predicate's
        // JSON membership probe only runs for the Tenant's unrouted Events. Dead-lettered Deliveries
        // are driven from this Tenant's Events and probe the Delivery index per row.
        // COUNT(*) returns bigint on Postgres, so every count is cast down to the int the DTO carries.
        return $"""
            SELECT
                awaiting.item_count AS AwaitingCount, awaiting.oldest_at AS AwaitingOldestAt,
                unrouted.item_count AS UnroutedCount, unrouted.oldest_at AS UnroutedOldestAt,
                dead.item_count AS DeadLetteredCount, dead.oldest_at AS DeadLetteredOldestAt
            FROM
                (SELECT CAST(COUNT(*) AS INT) AS item_count, MIN(e.accepted_at) AS oldest_at
                    FROM events e WHERE e.tenant_id = @TenantId AND e.status = 'accepted') awaiting
            CROSS JOIN
                (SELECT CAST(COUNT(*) AS INT) AS item_count, MIN(e.accepted_at) AS oldest_at
                    FROM events e WHERE e.tenant_id = @TenantId AND e.status = 'unrouted'
                      AND {UnroutedActionability.Predicate(sqlServer, "e")}) unrouted
            CROSS JOIN
                (SELECT CAST(COUNT(*) AS INT) AS item_count, MIN(d.failed_at) AS oldest_at
                    FROM event_deliveries d JOIN events e ON e.id = d.event_id
                    WHERE e.tenant_id = @TenantId AND d.status = 'dead_lettered') dead;
            """;
    }

    private sealed record BacklogRow
    {
        public int AwaitingCount { get; init; }
        public DateTimeOffset? AwaitingOldestAt { get; init; }
        public int UnroutedCount { get; init; }
        public DateTimeOffset? UnroutedOldestAt { get; init; }
        public int DeadLetteredCount { get; init; }
        public DateTimeOffset? DeadLetteredOldestAt { get; init; }
    }
}
