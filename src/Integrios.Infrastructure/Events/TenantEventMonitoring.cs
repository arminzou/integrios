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

    public async Task<IReadOnlyList<EventActivityBucketCounts>> GetActivityAsync(
        Guid tenantId,
        DateTimeOffset windowStart,
        TimeSpan bucketLength,
        int bucketCount,
        CancellationToken cancellationToken)
    {
        string sql = ActivitySql(connectionFactory.Provider == DatabaseProvider.SqlServer);
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        IEnumerable<EventActivityBucketCounts> rows = await connection.QueryAsync<EventActivityBucketCounts>(
            new CommandDefinition(sql, new
            {
                TenantId = tenantId,
                WindowStart = windowStart,
                WindowEnd = windowStart + bucketLength * bucketCount,
                BucketSeconds = (int)bucketLength.TotalSeconds,
            }, cancellationToken: cancellationToken));
        return rows.ToList();
    }

    internal static string ActivitySql(bool sqlServer)
    {
        // Whole seconds since the window start, floored by integer division. SQL Server's DATEDIFF
        // counts second boundaries crossed, which equals the floor only because the window start is
        // whole-second aligned; the handler guarantees that.
        string bucket = sqlServer
            ? "CAST(DATEDIFF_BIG(second, @WindowStart, e.accepted_at) / @BucketSeconds AS INT)"
            : "CAST(FLOOR(EXTRACT(EPOCH FROM (e.accepted_at - @WindowStart)) / @BucketSeconds) AS INT)";

        // The window is half-open, so an Event on a boundary lands in exactly one bucket. Statuses are
        // counted in one pass over the Tenant's window; Events with a dead-lettered Delivery are
        // counted separately as a semi-join, so several dead-lettered Deliveries never multiply an
        // Event and the probe runs once per Event rather than once per aggregate. Routed is what is
        // left of the non-backlog Events once those are taken out.
        string window = "e.tenant_id = @TenantId AND e.accepted_at >= @WindowStart AND e.accepted_at < @WindowEnd";
        return $"""
            SELECT
                statuses.bucket_index AS BucketIndex,
                statuses.awaiting AS AwaitingRouting,
                statuses.unrouted AS Unrouted,
                COALESCE(dead.dead_lettered, 0) AS DeliveryDeadLettered,
                statuses.settled - COALESCE(dead.dead_lettered, 0) AS Routed
            FROM (
                SELECT
                    bucketed.bucket_index,
                    CAST(SUM(CASE WHEN bucketed.status = 'accepted' THEN 1 ELSE 0 END) AS INT) AS awaiting,
                    CAST(SUM(CASE WHEN bucketed.status = 'unrouted' THEN 1 ELSE 0 END) AS INT) AS unrouted,
                    CAST(SUM(CASE WHEN bucketed.status IN ('accepted', 'unrouted') THEN 0 ELSE 1 END) AS INT) AS settled
                FROM (SELECT {bucket} AS bucket_index, e.status FROM events e WHERE {window}) bucketed
                GROUP BY bucketed.bucket_index
            ) statuses
            LEFT JOIN (
                SELECT bucketed.bucket_index, CAST(COUNT(*) AS INT) AS dead_lettered
                FROM (
                    SELECT {bucket} AS bucket_index FROM events e
                    WHERE {window} AND e.status NOT IN ('accepted', 'unrouted')
                      AND EXISTS (SELECT 1 FROM event_deliveries d WHERE d.event_id = e.id AND d.status = 'dead_lettered')
                ) bucketed
                GROUP BY bucketed.bucket_index
            ) dead ON dead.bucket_index = statuses.bucket_index
            ORDER BY statuses.bucket_index;
            """;
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
