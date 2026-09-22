using System.Data.Common;
using Dapper;
using Integrios.Application.Delivery;
using Integrios.Infrastructure.Data;

namespace Integrios.Infrastructure.Delivery;

internal sealed class SqlServerCompletedHistoryCleanup(IDbConnectionFactory connectionFactory)
    : ICompletedHistoryCleanup
{
    internal const string LockResource = "integrios:completed-history-retention";

    public async Task<CompletedHistoryCleanupResult> SweepAsync(
        TimeSpan retentionPeriod,
        int batchSize,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(retentionPeriod, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);

        await using DbConnection connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        int lockResult = await ExecuteLockAsync(connection, acquire: true, cancellationToken);
        if (lockResult < 0)
            return new(false, null, 0, 0);

        try
        {
            DateTime databaseNow = await connection.ExecuteScalarAsync<DateTime>(new CommandDefinition(
                "SELECT SYSUTCDATETIME()",
                cancellationToken: cancellationToken));
            DateTimeOffset cutoff = new(DateTime.SpecifyKind(databaseNow, DateTimeKind.Utc));
            cutoff -= retentionPeriod;

            int deletedEventCount = 0;
            int batchCount = 0;
            while (!cancellationToken.IsCancellationRequested)
            {
                int deleted = await DeleteBatchAsync(connection, cutoff, batchSize, cancellationToken);
                if (deleted == 0)
                    break;

                deletedEventCount += deleted;
                batchCount++;
                if (deleted < batchSize)
                    break;
            }

            cancellationToken.ThrowIfCancellationRequested();
            return new(true, cutoff, deletedEventCount, batchCount);
        }
        finally
        {
            await ExecuteLockAsync(connection, acquire: false, CancellationToken.None);
        }
    }

    private static async Task<int> DeleteBatchAsync(
        DbConnection connection,
        DateTimeOffset cutoff,
        int batchSize,
        CancellationToken cancellationToken)
    {
        await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);
        Guid[] eventIds = (await connection.QueryAsync<Guid>(new CommandDefinition(
            CandidateSql,
            new { Cutoff = cutoff.UtcDateTime, BatchSize = batchSize },
            transaction,
            cancellationToken: cancellationToken))).ToArray();
        if (eventIds.Length == 0)
        {
            await transaction.CommitAsync(cancellationToken);
            return 0;
        }

        await connection.QueryAsync<Guid>(new CommandDefinition(
            "SELECT id FROM outbox WITH (UPDLOCK, HOLDLOCK) WHERE event_id IN @EventIds",
            new { EventIds = eventIds },
            transaction,
            cancellationToken: cancellationToken));
        await connection.QueryAsync<Guid>(new CommandDefinition(
            "SELECT id FROM event_deliveries WITH (UPDLOCK, HOLDLOCK) WHERE event_id IN @EventIds",
            new { EventIds = eventIds },
            transaction,
            cancellationToken: cancellationToken));

        eventIds = (await connection.QueryAsync<Guid>(new CommandDefinition(
            EligibleSelectedSql,
            new { EventIds = eventIds, Cutoff = cutoff.UtcDateTime },
            transaction,
            cancellationToken: cancellationToken))).ToArray();
        if (eventIds.Length == 0)
        {
            await transaction.CommitAsync(cancellationToken);
            return 0;
        }

        await connection.ExecuteAsync(new CommandDefinition(
            """
            DELETE a FROM delivery_attempts a
            JOIN event_deliveries d ON d.id = a.event_delivery_id
            WHERE d.event_id IN @EventIds;
            DELETE FROM event_deliveries WHERE event_id IN @EventIds;
            DELETE FROM outbox WHERE event_id IN @EventIds AND processed_at IS NOT NULL;
            DELETE FROM events WHERE id IN @EventIds;
            """,
            new { EventIds = eventIds },
            transaction,
            cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return eventIds.Length;
    }

    private static Task<int> ExecuteLockAsync(
        DbConnection connection,
        bool acquire,
        CancellationToken cancellationToken) =>
        connection.ExecuteScalarAsync<int>(new CommandDefinition(
            acquire
                ? """
                  DECLARE @result int;
                  EXEC @result = sp_getapplock @Resource=@Resource, @LockMode='Exclusive',
                      @LockOwner='Session', @LockTimeout=0;
                  SELECT @result;
                  """
                : """
                  DECLARE @result int;
                  EXEC @result = sp_releaseapplock @Resource=@Resource, @LockOwner='Session';
                  SELECT @result;
                  """,
            new { Resource = LockResource },
            cancellationToken: cancellationToken));

    private const string CandidateSql =
        """
        SELECT TOP (@BatchSize) e.id
        FROM outbox retention_outbox
        JOIN events e WITH (UPDLOCK, READPAST, ROWLOCK) ON e.id = retention_outbox.event_id
        WHERE
            retention_outbox.processed_at IS NOT NULL
            AND retention_outbox.processed_at < @Cutoff
            AND
            NOT EXISTS (
                SELECT 1 FROM outbox o
                WHERE o.event_id = e.id AND o.processed_at IS NULL)
            AND (
                (e.status = N'unrouted' AND e.processed_at < @Cutoff)
                OR (
                    e.status = N'routed'
                    AND EXISTS (SELECT 1 FROM event_deliveries d WHERE d.event_id = e.id)
                    AND NOT EXISTS (
                        SELECT 1 FROM event_deliveries d
                        WHERE d.event_id = e.id AND d.status NOT IN (N'succeeded', N'dead_lettered'))
                    AND (
                        SELECT MAX(CASE WHEN d.status = N'succeeded' THEN d.processed_at ELSE d.failed_at END)
                        FROM event_deliveries d
                        WHERE d.event_id = e.id) < @Cutoff))
        ORDER BY retention_outbox.processed_at, e.id
        """;

    private const string EligibleSelectedSql =
        """
        SELECT e.id
        FROM events e
        WHERE e.id IN @EventIds
          AND NOT EXISTS (
              SELECT 1 FROM outbox o
              WHERE o.event_id = e.id AND o.processed_at IS NULL)
          AND (
              (e.status = N'unrouted' AND e.processed_at < @Cutoff)
              OR (
                  e.status = N'routed'
                  AND EXISTS (SELECT 1 FROM event_deliveries d WHERE d.event_id = e.id)
                  AND NOT EXISTS (
                      SELECT 1 FROM event_deliveries d
                      WHERE d.event_id = e.id AND d.status NOT IN (N'succeeded', N'dead_lettered'))
                  AND (
                      SELECT MAX(CASE WHEN d.status = N'succeeded' THEN d.processed_at ELSE d.failed_at END)
                      FROM event_deliveries d
                      WHERE d.event_id = e.id) < @Cutoff))
        """;
}
