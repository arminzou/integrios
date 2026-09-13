using Dapper;
using Integrios.Application.Ingestion;
using Integrios.Infrastructure.Data;

namespace Integrios.Infrastructure.Events;

internal sealed class TenantEventActivitySummary(IDbConnectionFactory connectionFactory) : ITenantEventActivitySummary
{
    public async Task<EventActivitySummaryCounts> GetAsync(
        Guid tenantId,
        TenantEventActivityFilter filter,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        CancellationToken cancellationToken)
    {
        var where = new List<string> { "e.tenant_id = @TenantId", "e.accepted_at >= @WindowStart", "e.accepted_at <= @WindowEnd" };
        if (filter.SourceId is not null) where.Add("e.source_id = @SourceId");
        if (filter.TopicId is not null) where.Add("e.topic_id = @TopicId");
        string eventWhere = string.Join(" AND ", where);

        // The dead-lettered count is a correlated subquery per windowed Event, not a JOIN: at
        // realistic volumes a plain join to event_deliveries drives the plan from the (deployment-wide)
        // dead_lettered rows and nested-loops every one against the small window, which measured over a
        // second against 500k Events and 25k dead-lettered Deliveries. Driving from the small,
        // index-covered window of Events and probing idx_event_deliveries_event_id per row keeps this
        // under 5ms at the same volume with no new index. See the design doc's Known Unknowns entry.
        // The correlated count is computed in a derived table rather than inline inside SUM(): SQL
        // Server rejects an aggregate whose argument itself contains a subquery, so the count first
        // becomes a plain column and only then gets summed.
        // Dapper's record materialization matches constructor parameter types exactly, and Postgres
        // COUNT(*) returns bigint, so every count is cast down to the int the DTO carries.
        string sql = $"""
            SELECT
                CAST(COUNT(*) AS INT) AS EventsAccepted,
                CAST(COUNT(CASE WHEN win.status = 'accepted' THEN 1 END) AS INT) AS AwaitingRouting,
                CAST(COUNT(CASE WHEN win.status = 'unrouted' THEN 1 END) AS INT) AS Unrouted,
                CAST(COALESCE(SUM(win.dead_lettered_count), 0) AS INT) AS DeadLetteredDeliveries
            FROM (
                SELECT
                    e.status,
                    (SELECT COUNT(*) FROM event_deliveries d WHERE d.event_id = e.id AND d.status = 'dead_lettered') AS dead_lettered_count
                FROM events e
                WHERE {eventWhere}
            ) win;
            """;

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        return await connection.QuerySingleAsync<EventActivitySummaryCounts>(new CommandDefinition(sql, new
        {
            TenantId = tenantId,
            WindowStart = windowStart,
            WindowEnd = windowEnd,
            filter.SourceId,
            filter.TopicId,
        }, cancellationToken: cancellationToken));
    }
}
