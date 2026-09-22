using System.Data.Common;
using Dapper;
using Integrios.Application.Delivery;
using Integrios.Infrastructure.Data;

namespace Integrios.Infrastructure.Delivery;

internal sealed class PostgresCompletedHistoryCleanup(IDbConnectionFactory connectionFactory)
    : ICompletedHistoryCleanup
{
    internal const long LockKey = 7_206_912_625_799_739_908;

    public async Task<CompletedHistoryCleanupResult> SweepAsync(
        TimeSpan retentionPeriod,
        int batchSize,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(retentionPeriod, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);

        await using DbConnection connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        bool acquired = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT pg_try_advisory_lock(@LockKey)",
            new { LockKey },
            cancellationToken: cancellationToken));
        if (!acquired)
            return new(false, null, 0, 0);

        try
        {
            DateTime databaseNow = await connection.ExecuteScalarAsync<DateTime>(new CommandDefinition(
                "SELECT now()",
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
            await connection.ExecuteAsync(new CommandDefinition(
                "SELECT pg_advisory_unlock(@LockKey)",
                new { LockKey }));
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
            new { Cutoff = cutoff, BatchSize = batchSize },
            transaction,
            cancellationToken: cancellationToken))).ToArray();
        if (eventIds.Length == 0)
        {
            await transaction.CommitAsync(cancellationToken);
            return 0;
        }

        await connection.QueryAsync<Guid>(new CommandDefinition(
            "SELECT id FROM outbox WHERE event_id = ANY(@EventIds) FOR UPDATE",
            new { EventIds = eventIds },
            transaction,
            cancellationToken: cancellationToken));
        await connection.QueryAsync<Guid>(new CommandDefinition(
            "SELECT id FROM event_deliveries WHERE event_id = ANY(@EventIds) FOR UPDATE",
            new { EventIds = eventIds },
            transaction,
            cancellationToken: cancellationToken));

        eventIds = (await connection.QueryAsync<Guid>(new CommandDefinition(
            EligibleSelectedSql,
            new { EventIds = eventIds, Cutoff = cutoff },
            transaction,
            cancellationToken: cancellationToken))).ToArray();
        if (eventIds.Length == 0)
        {
            await transaction.CommitAsync(cancellationToken);
            return 0;
        }

        await connection.ExecuteAsync(new CommandDefinition(
            """
            DELETE FROM delivery_attempts a
            USING event_deliveries d
            WHERE a.event_delivery_id = d.id AND d.event_id = ANY(@EventIds);
            DELETE FROM event_deliveries WHERE event_id = ANY(@EventIds);
            DELETE FROM outbox WHERE event_id = ANY(@EventIds) AND processed_at IS NOT NULL;
            DELETE FROM events WHERE id = ANY(@EventIds);
            """,
            new { EventIds = eventIds },
            transaction,
            cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return eventIds.Length;
    }

    private const string CandidateSql =
        """
        SELECT e.id
        FROM outbox retention_outbox
        JOIN events e ON e.id = retention_outbox.event_id
        WHERE
            retention_outbox.processed_at IS NOT NULL
            AND retention_outbox.processed_at < @Cutoff
            AND
            NOT EXISTS (
                SELECT 1 FROM outbox o
                WHERE o.event_id = e.id AND o.processed_at IS NULL)
            AND (
                (e.status = 'unrouted' AND e.processed_at < @Cutoff)
                OR (
                    e.status = 'routed'
                    AND EXISTS (SELECT 1 FROM event_deliveries d WHERE d.event_id = e.id)
                    AND NOT EXISTS (
                        SELECT 1 FROM event_deliveries d
                        WHERE d.event_id = e.id AND d.status NOT IN ('succeeded', 'dead_lettered'))
                    AND (
                        SELECT MAX(CASE WHEN d.status = 'succeeded' THEN d.processed_at ELSE d.failed_at END)
                        FROM event_deliveries d
                        WHERE d.event_id = e.id) < @Cutoff))
        ORDER BY retention_outbox.processed_at, e.id
        LIMIT @BatchSize
        FOR UPDATE OF e SKIP LOCKED
        """;

    private const string EligibleSelectedSql =
        """
        SELECT e.id
        FROM events e
        WHERE e.id = ANY(@EventIds)
          AND NOT EXISTS (
              SELECT 1 FROM outbox o
              WHERE o.event_id = e.id AND o.processed_at IS NULL)
          AND (
              (e.status = 'unrouted' AND e.processed_at < @Cutoff)
              OR (
                  e.status = 'routed'
                  AND EXISTS (SELECT 1 FROM event_deliveries d WHERE d.event_id = e.id)
                  AND NOT EXISTS (
                      SELECT 1 FROM event_deliveries d
                      WHERE d.event_id = e.id AND d.status NOT IN ('succeeded', 'dead_lettered'))
                  AND (
                      SELECT MAX(CASE WHEN d.status = 'succeeded' THEN d.processed_at ELSE d.failed_at END)
                      FROM event_deliveries d
                      WHERE d.event_id = e.id) < @Cutoff))
        """;
}
