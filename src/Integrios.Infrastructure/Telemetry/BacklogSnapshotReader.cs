using Dapper;
using Integrios.Infrastructure.Data;
using Integrios.Infrastructure.Delivery;

namespace Integrios.Infrastructure.Telemetry;

internal sealed class BacklogSnapshotReader(IDbConnectionFactory connectionFactory)
{
    public async Task<BacklogSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        string databaseNow = connectionFactory.Provider == DatabaseProvider.SqlServer
            ? "@DatabaseNow"
            : "clock.database_now";
        string claimable = EventDeliveryClaimability.Predicate(
            connectionFactory.Provider, "delivery", databaseNow);
        string deliveryAnchor = EventDeliveryClaimability.EligibilityAnchor(
            connectionFactory.Provider, "delivery");

        return await connection.QuerySingleAsync<BacklogSnapshot>(new CommandDefinition(
            BuildQuery(connectionFactory.Provider, claimable, deliveryAnchor),
            cancellationToken: cancellationToken));
    }

    private static string BuildQuery(
        DatabaseProvider provider,
        string claimable,
        string deliveryAnchor) => provider switch
    {
        DatabaseProvider.Postgres => $"""
            WITH clock AS (SELECT now() AS database_now),
            outbox_snapshot AS (
                SELECT COUNT(*) AS PendingOutboxDepth,
                    COALESCE(GREATEST(0, EXTRACT(EPOCH FROM MAX(clock.database_now) - MIN(COALESCE(outbox.deliver_after, outbox.created_at)))), 0)::double precision AS OldestPendingOutboxAgeSeconds
                FROM outbox CROSS JOIN clock
                WHERE outbox.processed_at IS NULL),
            delivery_snapshot AS (
                SELECT COUNT(*) AS ReadyDeliveryDepth,
                    COALESCE(GREATEST(0, EXTRACT(EPOCH FROM MAX(clock.database_now) - MIN({deliveryAnchor}))), 0)::double precision AS OldestReadyDeliveryAgeSeconds
                FROM event_deliveries delivery CROSS JOIN clock
                WHERE {claimable})
            SELECT PendingOutboxDepth, OldestPendingOutboxAgeSeconds,
                ReadyDeliveryDepth, OldestReadyDeliveryAgeSeconds
            FROM outbox_snapshot CROSS JOIN delivery_snapshot;
            """,
        DatabaseProvider.SqlServer => $"""
            DECLARE @DatabaseNow datetime2 = SYSUTCDATETIME();
            SELECT
                (SELECT COUNT_BIG(*) FROM outbox WHERE processed_at IS NULL) AS PendingOutboxDepth,
                CAST(COALESCE((
                    SELECT CASE WHEN MIN(COALESCE(outbox.deliver_after, outbox.created_at)) >= @DatabaseNow THEN 0
                        ELSE DATEDIFF_BIG(millisecond, MIN(COALESCE(outbox.deliver_after, outbox.created_at)), @DatabaseNow) / 1000.0 END
                    FROM outbox WHERE processed_at IS NULL), 0) AS float) AS OldestPendingOutboxAgeSeconds,
                (SELECT COUNT_BIG(*) FROM event_deliveries delivery WHERE {claimable}) AS ReadyDeliveryDepth,
                CAST(COALESCE((
                    SELECT CASE WHEN MIN({deliveryAnchor}) >= @DatabaseNow THEN 0
                        ELSE DATEDIFF_BIG(millisecond, MIN({deliveryAnchor}), @DatabaseNow) / 1000.0 END
                    FROM event_deliveries delivery WHERE {claimable}), 0) AS float) AS OldestReadyDeliveryAgeSeconds;
            """,
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null)
    };
}

internal sealed record BacklogSnapshot(
    long PendingOutboxDepth,
    double OldestPendingOutboxAgeSeconds,
    long ReadyDeliveryDepth,
    double OldestReadyDeliveryAgeSeconds);
