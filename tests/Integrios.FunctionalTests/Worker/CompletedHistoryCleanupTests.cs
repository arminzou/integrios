using System.Data.Common;
using Dapper;
using Integrios.Application.Delivery;
using Integrios.Application.Ingestion;
using Integrios.Infrastructure.Delivery;

namespace Integrios.FunctionalTests.Worker;

public sealed class CompletedHistoryCleanupTests(WorkerRoutingFixture fixture)
    : IClassFixture<WorkerRoutingFixture>, IAsyncLifetime
{
    private static readonly TimeSpan RetentionPeriod = TimeSpan.FromDays(7);
    private readonly DateTime old = DateTime.UtcNow.AddDays(-8);
    private readonly DateTime recent = DateTime.UtcNow.AddDays(-6);

    public Task InitializeAsync() => fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Sweep_DeletesOnlyOldTerminalAggregates_ByLatestTerminalTransition()
    {
        Guid oldUnrouted = await fixture.InsertOrphanEventAndOutboxAsync("payment.created");
        await fixture.RunFanoutBatchAsync();
        await SetRoutingCompletedAtAsync(oldUnrouted, old);

        Guid recentUnrouted = await fixture.InsertOrphanEventAndOutboxAsync("payment.created");
        await fixture.RunFanoutBatchAsync();
        await SetRoutingCompletedAtAsync(recentUnrouted, recent);

        Guid oldRouted = await fixture.InsertEventAndOutboxAsync("payment.multi");
        await fixture.RunFanoutBatchAsync();
        await SetOutboxProcessedAtAsync(oldRouted, old);
        Guid[] oldDeliveries = await DeliveryIdsAsync(oldRouted);
        await SetDeliveryTerminalAsync(oldDeliveries[0], "succeeded", old);
        await SetDeliveryTerminalAsync(oldDeliveries[1], "dead_lettered", old);
        await InsertAttemptAsync(oldDeliveries[1], "indeterminate", old);

        Guid recentRouted = await fixture.InsertEventAndOutboxAsync("payment.multi");
        await fixture.RunFanoutBatchAsync();
        await SetOutboxProcessedAtAsync(recentRouted, old);
        Guid[] recentDeliveries = await DeliveryIdsAsync(recentRouted);
        await SetDeliveryTerminalAsync(recentDeliveries[0], "succeeded", old);
        await SetDeliveryTerminalAsync(recentDeliveries[1], "dead_lettered", recent);

        // A sibling terminal delivery older than the cutoff makes the latest terminal transition old,
        // so only the non-terminal status blocks these aggregates.
        Guid pending = await fixture.InsertEventAndOutboxAsync("payment.multi");
        await fixture.RunFanoutBatchAsync();
        await SetOutboxProcessedAtAsync(pending, old);
        await SetDeliveryTerminalAsync((await DeliveryIdsAsync(pending))[0], "succeeded", old);

        Guid inFlight = await fixture.InsertEventAndOutboxAsync("payment.multi");
        await fixture.RunFanoutBatchAsync();
        await SetOutboxProcessedAtAsync(inFlight, old);
        Guid[] inFlightDeliveries = await DeliveryIdsAsync(inFlight);
        await SetDeliveryTerminalAsync(inFlightDeliveries[0], "succeeded", old);
        await SetDeliveryInFlightAsync(inFlightDeliveries[1]);

        Guid unprocessedOutbox = await fixture.InsertEventAndOutboxAsync("payment.created");
        await ExecuteAsync(
            "UPDATE events SET status='unrouted', processed_at=@Old WHERE id=@EventId",
            new { Old = old, EventId = unprocessedOutbox });

        CompletedHistoryCleanupResult result = await fixture.CompletedHistoryCleanup.SweepAsync(
            RetentionPeriod, 1, CancellationToken.None);

        result.Acquired.ShouldBeTrue();
        result.DeletedEventCount.ShouldBe(2);
        result.BatchCount.ShouldBe(2);
        (await EventExistsAsync(oldUnrouted)).ShouldBeFalse();
        (await EventExistsAsync(oldRouted)).ShouldBeFalse();
        (await ScalarAsync<int>(
            "SELECT COUNT(*) FROM delivery_attempts WHERE event_delivery_id=@DeliveryId",
            new { DeliveryId = oldDeliveries[1] })).ShouldBe(0);
        (await EventExistsAsync(recentUnrouted)).ShouldBeTrue();
        (await EventExistsAsync(recentRouted)).ShouldBeTrue();
        (await EventExistsAsync(pending)).ShouldBeTrue();
        (await EventExistsAsync(inFlight)).ShouldBeTrue();
        (await EventExistsAsync(unprocessedOutbox)).ShouldBeTrue();
        (await CanAcquireRetentionLockAsync()).ShouldBeTrue();
    }

    [Fact]
    public async Task Sweep_FailureRollsBackTheWholeAggregate()
    {
        Guid eventId = await fixture.InsertEventAndOutboxAsync("payment.created");
        await fixture.RunFanoutBatchAsync();
        await SetOutboxProcessedAtAsync(eventId, old);
        Guid deliveryId = (await DeliveryIdsAsync(eventId)).Single();
        await SetDeliveryTerminalAsync(deliveryId, "succeeded", old);
        await InsertAttemptAsync(deliveryId, "succeeded", old);

        await WithEventDeleteFailureAsync(async () =>
            await Should.ThrowAsync<DbException>(() => fixture.CompletedHistoryCleanup.SweepAsync(
                RetentionPeriod, 10, CancellationToken.None)));

        (await EventExistsAsync(eventId)).ShouldBeTrue();
        (await ScalarAsync<int>("SELECT COUNT(*) FROM outbox WHERE event_id=@EventId", new { EventId = eventId })).ShouldBe(1);
        (await ScalarAsync<int>("SELECT COUNT(*) FROM event_deliveries WHERE event_id=@EventId", new { EventId = eventId })).ShouldBe(1);
        (await ScalarAsync<int>("SELECT COUNT(*) FROM delivery_attempts WHERE event_delivery_id=@DeliveryId", new { DeliveryId = deliveryId })).ShouldBe(1);
        (await CanAcquireRetentionLockAsync()).ShouldBeTrue();
    }

    [Fact]
    public async Task Sweep_WhenAnotherReplicaOwnsTheLease_IsANoOp()
    {
        await using DbConnection connection = fixture.CreateConnection();
        await connection.OpenAsync();
        await AcquireRetentionLockAsync(connection);
        try
        {
            CompletedHistoryCleanupResult result = await fixture.CompletedHistoryCleanup.SweepAsync(
                RetentionPeriod, 10, CancellationToken.None);

            result.ShouldBe(new CompletedHistoryCleanupResult(false, null, 0, 0));
        }
        finally
        {
            await ReleaseRetentionLockAsync(connection);
        }
    }

    [Fact]
    public async Task Sweep_ContinuesAfterPostLockRecheckRemovesAFullBatch()
    {
        Guid changed = await fixture.InsertEventAndOutboxAsync("payment.multi");
        Guid later = await fixture.InsertEventAndOutboxAsync("payment.multi");
        await fixture.RunFanoutBatchAsync();
        await SetOutboxProcessedAtAsync(changed, old.AddMinutes(-2));
        await SetOutboxProcessedAtAsync(later, old.AddMinutes(-1));
        Guid[] changedDeliveries = await DeliveryIdsAsync(changed);
        Guid[] laterDeliveries = await DeliveryIdsAsync(later);
        foreach (Guid deliveryId in changedDeliveries.Concat(laterDeliveries))
            await SetDeliveryTerminalAsync(deliveryId, "succeeded", old);

        await using DbConnection control = fixture.CreateConnection();
        await control.OpenAsync();
        await using DbTransaction transaction = await control.BeginTransactionAsync();
        await LockDeliveryAsync(control, transaction, changedDeliveries[0]);

        Task<CompletedHistoryCleanupResult> sweeping = fixture.CompletedHistoryCleanup.SweepAsync(
            RetentionPeriod, 1, CancellationToken.None);
        await WaitUntilEventLockedAsync(changed);
        await control.ExecuteAsync(
            "UPDATE event_deliveries SET status='pending', processed_at=NULL, failed_at=NULL WHERE id=@DeliveryId",
            new { DeliveryId = changedDeliveries[0] },
            transaction);
        await transaction.CommitAsync();

        CompletedHistoryCleanupResult result = await sweeping;

        result.DeletedEventCount.ShouldBe(1);
        result.BatchCount.ShouldBe(2);
        (await EventExistsAsync(changed)).ShouldBeTrue();
        (await EventExistsAsync(later)).ShouldBeFalse();
    }

    [Fact]
    public async Task Sweep_EndsDeduplicationWithEvent()
    {
        const string idempotencyKey = "retention-idempotency";
        EventAcceptance first = await fixture.AcceptEventAsync(idempotencyKey);
        EventAcceptance duplicate = await fixture.AcceptEventAsync(idempotencyKey);
        duplicate.AlreadyAccepted.ShouldBeTrue();
        duplicate.EventId.ShouldBe(first.EventId);

        await fixture.RunFanoutBatchAsync();
        await SetOutboxProcessedAtAsync(first.EventId, old);
        foreach (Guid deliveryId in await DeliveryIdsAsync(first.EventId))
            await SetDeliveryTerminalAsync(deliveryId, "succeeded", old);

        CompletedHistoryCleanupResult result = await fixture.CompletedHistoryCleanup.SweepAsync(
            RetentionPeriod, 10, CancellationToken.None);
        EventAcceptance acceptedAgain = await fixture.AcceptEventAsync(idempotencyKey);

        result.DeletedEventCount.ShouldBe(1);
        acceptedAgain.AlreadyAccepted.ShouldBeFalse();
        acceptedAgain.EventId.ShouldNotBe(first.EventId);
    }

    [Fact]
    public async Task Sweep_PreservesControlPlaneTombstones()
    {
        Guid eventId = await fixture.InsertEventAndOutboxAsync("payment.multi");
        await fixture.RunFanoutBatchAsync();
        await SetOutboxProcessedAtAsync(eventId, old);
        Guid[] deliveryIds = await DeliveryIdsAsync(eventId);
        foreach (Guid deliveryId in deliveryIds)
            await SetDeliveryTerminalAsync(deliveryId, "succeeded", old);

        Guid sourceId = await ScalarAsync<Guid>("SELECT source_id FROM events WHERE id=@EventId", new { EventId = eventId });
        Guid topicId = await ScalarAsync<Guid>("SELECT topic_id FROM events WHERE id=@EventId", new { EventId = eventId });
        Guid[] subscriptionIds = (await QueryAsync<Guid>(
            "SELECT subscription_id FROM event_deliveries WHERE event_id=@EventId", new { EventId = eventId })).ToArray();
        Guid[] destinationIds = (await QueryAsync<Guid>(
            "SELECT destination_id FROM event_deliveries WHERE event_id=@EventId", new { EventId = eventId })).ToArray();

        await ExecuteAsync("UPDATE sources SET deleted_at=@Now WHERE id=@SourceId", new { Now = DateTime.UtcNow, SourceId = sourceId });
        await ExecuteAsync("UPDATE topics SET deleted_at=@Now WHERE id=@TopicId", new { Now = DateTime.UtcNow, TopicId = topicId });
        await ExecuteAsync($"UPDATE subscriptions SET deleted_at=@Now WHERE {Ids("id")}", new { Now = DateTime.UtcNow, Ids = subscriptionIds });
        await ExecuteAsync($"UPDATE destinations SET deleted_at=@Now WHERE {Ids("id")}", new { Now = DateTime.UtcNow, Ids = destinationIds });

        CompletedHistoryCleanupResult result = await fixture.CompletedHistoryCleanup.SweepAsync(
            RetentionPeriod, 10, CancellationToken.None);

        result.DeletedEventCount.ShouldBe(1);
        (await ScalarAsync<int>("SELECT COUNT(*) FROM sources WHERE id=@Id AND deleted_at IS NOT NULL", new { Id = sourceId })).ShouldBe(1);
        (await ScalarAsync<int>("SELECT COUNT(*) FROM topics WHERE id=@Id AND deleted_at IS NOT NULL", new { Id = topicId })).ShouldBe(1);
        (await ScalarAsync<int>($"SELECT COUNT(*) FROM subscriptions WHERE {Ids("id")} AND deleted_at IS NOT NULL", new { Ids = subscriptionIds })).ShouldBe(subscriptionIds.Length);
        (await ScalarAsync<int>($"SELECT COUNT(*) FROM destinations WHERE {Ids("id")} AND deleted_at IS NOT NULL", new { Ids = destinationIds })).ShouldBe(destinationIds.Length);
    }

    private Task SetRoutingCompletedAtAsync(Guid eventId, DateTime processedAt) => ExecuteAsync(
        "UPDATE events SET processed_at=@ProcessedAt WHERE id=@EventId; UPDATE outbox SET processed_at=@ProcessedAt WHERE event_id=@EventId",
        new { ProcessedAt = processedAt, EventId = eventId });

    private Task SetOutboxProcessedAtAsync(Guid eventId, DateTime processedAt) => ExecuteAsync(
        "UPDATE outbox SET processed_at=@ProcessedAt WHERE event_id=@EventId",
        new { ProcessedAt = processedAt, EventId = eventId });

    private Task SetDeliveryTerminalAsync(Guid deliveryId, string status, DateTime terminalAt) => ExecuteAsync(
        """
        UPDATE event_deliveries
        SET status=@Status,
            processed_at=CASE WHEN @Status='succeeded' THEN @TerminalAt ELSE NULL END,
            failed_at=CASE WHEN @Status='dead_lettered' THEN @TerminalAt ELSE NULL END,
            active_attempt_id=NULL, lease_expires_at=NULL, updated_at=@TerminalAt
        WHERE id=@DeliveryId
        """,
        new { DeliveryId = deliveryId, Status = status, TerminalAt = terminalAt });

    private async Task InsertAttemptAsync(Guid deliveryId, string status, DateTime completedAt)
    {
        int attemptNumber = await ScalarAsync<int>(
            "SELECT COUNT(*) + 1 FROM delivery_attempts WHERE event_delivery_id=@DeliveryId",
            new { DeliveryId = deliveryId });
        await ExecuteAsync(
            """
            INSERT INTO delivery_attempts
                (id,event_delivery_id,attempt_number,status,started_at,completed_at)
            VALUES (@Id,@DeliveryId,@AttemptNumber,@Status,@CompletedAt,@CompletedAt)
            """,
            new { Id = Guid.NewGuid(), DeliveryId = deliveryId, AttemptNumber = attemptNumber, Status = status, CompletedAt = completedAt });
    }

    private async Task SetDeliveryInFlightAsync(Guid deliveryId)
    {
        Guid attemptId = Guid.NewGuid();
        await ExecuteAsync(
            """
            INSERT INTO delivery_attempts (id,event_delivery_id,attempt_number,status,started_at)
            VALUES (@AttemptId,@DeliveryId,1,'in_progress',@Now);
            UPDATE event_deliveries
            SET status='in_flight', active_attempt_id=@AttemptId, lease_expires_at=@LeaseExpiresAt,
                lifetime_attempt_count=1, retry_cycle_attempt_count=1
            WHERE id=@DeliveryId;
            """,
            new { AttemptId = attemptId, DeliveryId = deliveryId, Now = DateTime.UtcNow, LeaseExpiresAt = DateTime.UtcNow.AddHours(1) });
    }

    private async Task<Guid[]> DeliveryIdsAsync(Guid eventId) =>
        (await QueryAsync<Guid>("SELECT id FROM event_deliveries WHERE event_id=@EventId ORDER BY id", new { EventId = eventId })).ToArray();

    private string Ids(string column) => fixture.DatabaseProvider == "postgres"
        ? $"{column} = ANY(@Ids)"
        : $"{column} IN @Ids";

    private async Task<bool> EventExistsAsync(Guid eventId) =>
        await ScalarAsync<int>("SELECT COUNT(*) FROM events WHERE id=@EventId", new { EventId = eventId }) == 1;

    private async Task WithEventDeleteFailureAsync(Func<Task> action)
    {
        string install = fixture.DatabaseProvider == "postgres"
            ? """
              CREATE FUNCTION fail_retention_event_delete() RETURNS trigger AS $$
              BEGIN RAISE EXCEPTION 'injected retention failure'; END;
              $$ LANGUAGE plpgsql;
              CREATE TRIGGER fail_retention_event_delete BEFORE DELETE ON events
                  FOR EACH ROW EXECUTE FUNCTION fail_retention_event_delete();
              """
            : """
              CREATE TRIGGER fail_retention_event_delete ON events AFTER DELETE AS
              BEGIN THROW 51003, 'injected retention failure', 1; END
              """;
        string cleanup = fixture.DatabaseProvider == "postgres"
            ? "DROP TRIGGER IF EXISTS fail_retention_event_delete ON events; DROP FUNCTION IF EXISTS fail_retention_event_delete();"
            : "DROP TRIGGER IF EXISTS fail_retention_event_delete";
        await ExecuteAsync(install);
        try
        {
            await action();
        }
        finally
        {
            await ExecuteAsync(cleanup);
        }
    }

    private Task AcquireRetentionLockAsync(DbConnection connection) => fixture.DatabaseProvider == "postgres"
        ? connection.ExecuteAsync("SELECT pg_advisory_lock(@Key)", new { Key = PostgresCompletedHistoryCleanup.LockKey })
        : connection.ExecuteAsync(
            "DECLARE @r int; EXEC @r=sp_getapplock @Resource=@Resource, @LockMode='Exclusive', @LockOwner='Session';",
            new { Resource = SqlServerCompletedHistoryCleanup.LockResource });

    private Task ReleaseRetentionLockAsync(DbConnection connection) => fixture.DatabaseProvider == "postgres"
        ? connection.ExecuteAsync("SELECT pg_advisory_unlock(@Key)", new { Key = PostgresCompletedHistoryCleanup.LockKey })
        : connection.ExecuteAsync(
            "DECLARE @r int; EXEC @r=sp_releaseapplock @Resource=@Resource, @LockOwner='Session';",
            new { Resource = SqlServerCompletedHistoryCleanup.LockResource });

    private async Task<bool> CanAcquireRetentionLockAsync()
    {
        await using DbConnection connection = fixture.CreateConnection();
        await connection.OpenAsync();
        if (fixture.DatabaseProvider == "postgres")
        {
            bool acquired = await connection.ExecuteScalarAsync<bool>(
                "SELECT pg_try_advisory_lock(@Key)", new { Key = PostgresCompletedHistoryCleanup.LockKey });
            if (acquired)
                await ReleaseRetentionLockAsync(connection);
            return acquired;
        }

        int result = await connection.ExecuteScalarAsync<int>(
            "DECLARE @r int; EXEC @r=sp_getapplock @Resource=@Resource, @LockMode='Exclusive', @LockOwner='Session', @LockTimeout=0; SELECT @r;",
            new { Resource = SqlServerCompletedHistoryCleanup.LockResource });
        if (result >= 0)
            await ReleaseRetentionLockAsync(connection);
        return result >= 0;
    }

    private Task LockDeliveryAsync(DbConnection connection, DbTransaction transaction, Guid deliveryId) =>
        connection.ExecuteAsync(
            fixture.DatabaseProvider == "postgres"
                ? "SELECT id FROM event_deliveries WHERE id=@DeliveryId FOR UPDATE"
                : "SELECT id FROM event_deliveries WITH (UPDLOCK, HOLDLOCK, INDEX(0)) WHERE id=@DeliveryId",
            new { DeliveryId = deliveryId },
            transaction);

    private async Task WaitUntilEventLockedAsync(Guid eventId)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            await using DbConnection probe = fixture.CreateConnection();
            await probe.OpenAsync();
            try
            {
                await probe.ExecuteAsync(
                    fixture.DatabaseProvider == "postgres"
                        ? "SELECT id FROM events WHERE id=@EventId FOR UPDATE NOWAIT"
                        : "SET LOCK_TIMEOUT 0; SELECT id FROM events WITH (UPDLOCK, HOLDLOCK) WHERE id=@EventId",
                    new { EventId = eventId });
            }
            catch (DbException)
            {
                return;
            }

            await Task.Delay(20);
        }

        throw new TimeoutException("The retention sweep did not lock the selected Event.");
    }

    private async Task<int> ExecuteAsync(string sql, object? parameters = null)
    {
        await using DbConnection connection = fixture.CreateConnection();
        await connection.OpenAsync();
        return await connection.ExecuteAsync(sql, parameters);
    }

    private async Task<T> ScalarAsync<T>(string sql, object? parameters = null)
    {
        await using DbConnection connection = fixture.CreateConnection();
        await connection.OpenAsync();
        return (await connection.ExecuteScalarAsync<T>(sql, parameters))!;
    }

    private async Task<IEnumerable<T>> QueryAsync<T>(string sql, object? parameters = null)
    {
        await using DbConnection connection = fixture.CreateConnection();
        await connection.OpenAsync();
        return await connection.QueryAsync<T>(sql, parameters);
    }
}
