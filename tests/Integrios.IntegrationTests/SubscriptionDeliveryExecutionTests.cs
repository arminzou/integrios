using System.Diagnostics;
using Integrios.Application.Delivery;
using Integrios.Domain.Delivery;
using Npgsql;

namespace Integrios.IntegrationTests;

public sealed class SubscriptionDeliveryExecutionTests : IClassFixture<WorkerRoutingFixture>, IAsyncLifetime
{
    private readonly WorkerRoutingFixture fixture;

    public SubscriptionDeliveryExecutionTests(WorkerRoutingFixture fixture)
    {
        this.fixture = fixture;
    }

    public async Task InitializeAsync() => await fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task ClaimNext_CreatesAttemptAndAdvancesDeliveryAtomically()
    {
        Guid deliveryId = await fixture.FanoutSingleDeliveryAsync();

        SubscriptionDeliveryWorkItem? claimed = await fixture.DeliveryQueue.ClaimNextAsync(CancellationToken.None);

        Assert.NotNull(claimed);
        Assert.Equal(deliveryId, claimed.Id);
        Assert.Equal(1, claimed.AttemptNumber);

        SubscriptionDeliveryState delivery = await fixture.GetSubscriptionDeliveryAsync(deliveryId);
        Assert.Equal("in_flight", delivery.Status);
        Assert.Equal(1, delivery.LifetimeAttemptCount);
        Assert.Equal(1, delivery.RetryCycleAttemptCount);
        Assert.Equal(claimed.AttemptId, delivery.ActiveAttemptId);
        Assert.NotNull(delivery.LeaseExpiresAt);

        DeliveryAttemptState attempt = Assert.Single(await fixture.GetDeliveryAttemptsAsync(deliveryId));
        Assert.Equal(claimed.AttemptId, attempt.Id);
        Assert.Equal(deliveryId, attempt.SubscriptionDeliveryId);
        Assert.Equal(1, attempt.AttemptNumber);
        Assert.Equal("in_progress", attempt.Status);
        Assert.Null(attempt.CompletedAt);
    }

    [Fact]
    public async Task ClaimNext_CompetingClaimsProduceOneOwnerAndOneAttempt()
    {
        Guid deliveryId = await fixture.FanoutSingleDeliveryAsync();

        SubscriptionDeliveryWorkItem?[] claims = await Task.WhenAll(
            fixture.DeliveryQueue.ClaimNextAsync(CancellationToken.None),
            fixture.DeliveryQueue.ClaimNextAsync(CancellationToken.None));

        SubscriptionDeliveryWorkItem claimed = Assert.Single(claims, claim => claim is not null)!;
        Assert.Equal(deliveryId, claimed.Id);
        Assert.Single(await fixture.GetDeliveryAttemptsAsync(deliveryId));

        SubscriptionDeliveryState delivery = await fixture.GetSubscriptionDeliveryAsync(deliveryId);
        Assert.Equal(claimed.AttemptId, delivery.ActiveAttemptId);
        Assert.Equal(1, delivery.LifetimeAttemptCount);
    }

    [Fact]
    public async Task ClaimNext_WhenDeliveryAdvanceFails_RollsBackAttemptAndClaim()
    {
        Guid deliveryId = await fixture.FanoutSingleDeliveryAsync();
        await ExecuteSqlAsync(
            """
            CREATE FUNCTION test_fail_delivery_claim() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                RAISE EXCEPTION 'injected claim failure';
            END $$;
            CREATE TRIGGER test_fail_delivery_claim
                BEFORE UPDATE ON subscription_deliveries
                FOR EACH ROW
                WHEN (OLD.status = 'pending' AND NEW.status = 'in_flight')
                EXECUTE FUNCTION test_fail_delivery_claim();
            """);

        try
        {
            await Assert.ThrowsAsync<PostgresException>(() => fixture.DeliveryQueue.ClaimNextAsync(CancellationToken.None));
        }
        finally
        {
            await ExecuteSqlAsync(
                """
                DROP TRIGGER IF EXISTS test_fail_delivery_claim ON subscription_deliveries;
                DROP FUNCTION IF EXISTS test_fail_delivery_claim();
                """);
        }

        SubscriptionDeliveryState delivery = await fixture.GetSubscriptionDeliveryAsync(deliveryId);
        Assert.Equal("pending", delivery.Status);
        Assert.Equal(0, delivery.LifetimeAttemptCount);
        Assert.Equal(0, delivery.RetryCycleAttemptCount);
        Assert.Null(delivery.ActiveAttemptId);
        Assert.Empty(await fixture.GetDeliveryAttemptsAsync(deliveryId));
    }

    [Fact]
    public async Task Finalize_WhenDeliveryAdvanceFails_RollsBackAttemptAndDelivery()
    {
        Guid deliveryId = await fixture.FanoutSingleDeliveryAsync();
        SubscriptionDeliveryWorkItem claimed = Assert.IsType<SubscriptionDeliveryWorkItem>(
            await fixture.DeliveryQueue.ClaimNextAsync(CancellationToken.None));
        await ExecuteSqlAsync(
            """
            CREATE FUNCTION test_fail_delivery_finalization() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                RAISE EXCEPTION 'injected finalization failure';
            END $$;
            CREATE TRIGGER test_fail_delivery_finalization
                BEFORE UPDATE ON subscription_deliveries
                FOR EACH ROW
                WHEN (OLD.status = 'in_flight' AND NEW.status <> 'in_flight')
                EXECUTE FUNCTION test_fail_delivery_finalization();
            """);

        try
        {
            await Assert.ThrowsAsync<PostgresException>(() =>
                fixture.DeliveryQueue.FinalizeAsync(SuccessfulCompletion(claimed), CancellationToken.None));
        }
        finally
        {
            await ExecuteSqlAsync(
                """
                DROP TRIGGER IF EXISTS test_fail_delivery_finalization ON subscription_deliveries;
                DROP FUNCTION IF EXISTS test_fail_delivery_finalization();
                """);
        }

        SubscriptionDeliveryState delivery = await fixture.GetSubscriptionDeliveryAsync(deliveryId);
        Assert.Equal("in_flight", delivery.Status);
        Assert.Equal(claimed.AttemptId, delivery.ActiveAttemptId);
        Assert.NotNull(delivery.LeaseExpiresAt);

        DeliveryAttemptState attempt = Assert.Single(await fixture.GetDeliveryAttemptsAsync(deliveryId));
        Assert.Equal("in_progress", attempt.Status);
        Assert.Null(attempt.CompletedAt);
    }

    [Fact]
    public async Task ExpiredLease_ReclaimsWithNewFenceAndRejectsStaleFinalization()
    {
        Guid deliveryId = await fixture.FanoutSingleDeliveryAsync();
        SubscriptionDeliveryWorkItem? firstClaim = await fixture.DeliveryQueue.ClaimNextAsync(CancellationToken.None);
        Assert.NotNull(firstClaim);
        SubscriptionDeliveryWorkItem first = firstClaim;
        await fixture.ForceLeaseExpiredAsync(deliveryId);

        SubscriptionDeliveryWorkItem? secondClaim = await fixture.DeliveryQueue.ClaimNextAsync(CancellationToken.None);
        Assert.NotNull(secondClaim);
        SubscriptionDeliveryWorkItem second = secondClaim;

        Assert.NotEqual(first.AttemptId, second.AttemptId);
        Assert.Equal(2, second.AttemptNumber);
        IReadOnlyList<DeliveryAttemptState> attemptsAfterReclaim = await fixture.GetDeliveryAttemptsAsync(deliveryId);
        Assert.Equal(["indeterminate", "in_progress"], attemptsAfterReclaim.Select(attempt => attempt.Status));

        DeliveryFinalizationResult staleResult = await fixture.DeliveryQueue.FinalizeAsync(
            SuccessfulCompletion(first),
            CancellationToken.None);

        Assert.Equal(DeliveryFinalizationStatus.OwnershipLost, staleResult.Status);
        SubscriptionDeliveryState stillOwned = await fixture.GetSubscriptionDeliveryAsync(deliveryId);
        Assert.Equal("in_flight", stillOwned.Status);
        Assert.Equal(second.AttemptId, stillOwned.ActiveAttemptId);

        DeliveryFinalizationResult activeResult = await fixture.DeliveryQueue.FinalizeAsync(
            SuccessfulCompletion(second),
            CancellationToken.None);
        Assert.Equal(DeliveryFinalizationStatus.Applied, activeResult.Status);
        Assert.Equal(SubscriptionDeliveryDisposition.Succeeded, activeResult.Disposition);

        SubscriptionDeliveryState succeeded = await fixture.GetSubscriptionDeliveryAsync(deliveryId);
        Assert.Equal("succeeded", succeeded.Status);
        Assert.Null(succeeded.ActiveAttemptId);
        Assert.Null(succeeded.LeaseExpiresAt);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExpiredLease_ReclaimAndFinalizationRace_HonorsTheRowLockWinner(bool finalizationWins)
    {
        const long advisoryLockKey = 8_931_047_221;
        Guid deliveryId = await fixture.FanoutSingleDeliveryAsync();
        SubscriptionDeliveryWorkItem first = Assert.IsType<SubscriptionDeliveryWorkItem>(
            await fixture.DeliveryQueue.ClaimNextAsync(CancellationToken.None));
        await fixture.ForceLeaseExpiredAsync(deliveryId);

        string blockedStatus = finalizationWins ? "succeeded" : "indeterminate";
        await InstallRaceBarrierAsync(blockedStatus, advisoryLockKey);

        await using var barrierConnection = new NpgsqlConnection(fixture.ConnectionString);
        await barrierConnection.OpenAsync();
        await ExecuteScalarAsync(barrierConnection, "SELECT pg_advisory_lock(@LockKey)", advisoryLockKey);

        Task<DeliveryFinalizationResult>? finalizationTask = null;
        Task<SubscriptionDeliveryWorkItem?>? reclaimTask = null;

        try
        {
            if (finalizationWins)
                finalizationTask = fixture.DeliveryQueue.FinalizeAsync(SuccessfulCompletion(first), CancellationToken.None);
            else
                reclaimTask = fixture.DeliveryQueue.ClaimNextAsync(CancellationToken.None);

            await WaitForRaceBarrierAsync();

            if (finalizationWins)
            {
                reclaimTask = fixture.DeliveryQueue.ClaimNextAsync(CancellationToken.None);
                Assert.Null(await reclaimTask);
            }
            else
            {
                finalizationTask = fixture.DeliveryQueue.FinalizeAsync(SuccessfulCompletion(first), CancellationToken.None);
                await WaitForBlockedFinalizationAsync();
            }

            await ExecuteScalarAsync(barrierConnection, "SELECT pg_advisory_unlock(@LockKey)", advisoryLockKey);

            DeliveryFinalizationResult finalization = await finalizationTask!;
            SubscriptionDeliveryWorkItem? reclaim = await reclaimTask!;

            if (finalizationWins)
            {
                Assert.Equal(DeliveryFinalizationStatus.Applied, finalization.Status);
                Assert.Equal(SubscriptionDeliveryDisposition.Succeeded, finalization.Disposition);
                Assert.Null(reclaim);
                Assert.Equal("succeeded", (await fixture.GetSubscriptionDeliveryAsync(deliveryId)).Status);
                Assert.Equal("succeeded", Assert.Single(await fixture.GetDeliveryAttemptsAsync(deliveryId)).Status);
            }
            else
            {
                Assert.Equal(DeliveryFinalizationStatus.OwnershipLost, finalization.Status);
                Assert.NotNull(reclaim);
                Assert.Equal(2, reclaim.AttemptNumber);
                Assert.Equal(
                    ["indeterminate", "in_progress"],
                    (await fixture.GetDeliveryAttemptsAsync(deliveryId)).Select(attempt => attempt.Status));
            }
        }
        finally
        {
            await ExecuteScalarAsync(barrierConnection, "SELECT pg_advisory_unlock(@LockKey)", advisoryLockKey);
            if (finalizationTask is not null)
                await finalizationTask;
            if (reclaimTask is not null)
                await reclaimTask;
            await ExecuteSqlAsync(
                """
                DROP TRIGGER IF EXISTS test_block_delivery_attempt_update ON delivery_attempts;
                DROP FUNCTION IF EXISTS test_block_delivery_attempt_update();
                DROP SEQUENCE IF EXISTS test_delivery_race_sequence;
                """);
        }
    }

    [Fact]
    public async Task RepeatedExpiredLeases_ConsumeBudgetAndDeadLetterWithoutExtraAttempt()
    {
        Guid deliveryId = await fixture.FanoutSingleDeliveryAsync();

        for (int expectedAttempt = 1; expectedAttempt <= RetryPolicy.DefaultMaxAttempts; expectedAttempt++)
        {
            SubscriptionDeliveryWorkItem? claim = await fixture.DeliveryQueue.ClaimNextAsync(CancellationToken.None);
            Assert.NotNull(claim);
            SubscriptionDeliveryWorkItem claimed = claim;
            Assert.Equal(expectedAttempt, claimed.AttemptNumber);
            await fixture.ForceLeaseExpiredAsync(deliveryId);
        }

        var recovery = Assert.IsType<RecoveredSubscriptionDeliveryDeadLetter>(
            await fixture.DeliveryQueue.ClaimNextWithRecoveryAsync(CancellationToken.None));
        Assert.Equal(deliveryId, recovery.DeliveryId);
        Assert.Equal(RetryPolicy.DefaultMaxAttempts, recovery.AttemptNumber);
        Assert.NotEqual(Guid.Empty, recovery.AttemptId);
        Assert.Null(await fixture.DeliveryQueue.ClaimNextAsync(CancellationToken.None));

        SubscriptionDeliveryState delivery = await fixture.GetSubscriptionDeliveryAsync(deliveryId);
        Assert.Equal("dead_lettered", delivery.Status);
        Assert.Equal(RetryPolicy.DefaultMaxAttempts, delivery.LifetimeAttemptCount);
        Assert.Equal(RetryPolicy.DefaultMaxAttempts, delivery.RetryCycleAttemptCount);
        Assert.Null(delivery.ActiveAttemptId);
        Assert.Null(delivery.LeaseExpiresAt);

        IReadOnlyList<DeliveryAttemptState> attempts = await fixture.GetDeliveryAttemptsAsync(deliveryId);
        Assert.Equal(RetryPolicy.DefaultMaxAttempts, attempts.Count);
        Assert.Equal([1, 2, 3], attempts.Select(attempt => attempt.AttemptNumber));
        Assert.All(attempts, attempt => Assert.Equal("indeterminate", attempt.Status));
    }

    [Fact]
    public async Task ExhaustedExpiredLease_DoesNotHideLaterPendingWork()
    {
        Guid exhaustedDeliveryId = await fixture.FanoutSingleDeliveryAsync();

        for (int attemptNumber = 1; attemptNumber <= RetryPolicy.DefaultMaxAttempts; attemptNumber++)
        {
            SubscriptionDeliveryWorkItem claimed = Assert.IsType<SubscriptionDeliveryWorkItem>(
                await fixture.DeliveryQueue.ClaimNextAsync(CancellationToken.None));
            Assert.Equal(exhaustedDeliveryId, claimed.Id);
            await fixture.ForceLeaseExpiredAsync(exhaustedDeliveryId);
        }

        Guid pendingDeliveryId = await fixture.FanoutSingleDeliveryAsync();

        SubscriptionDeliveryWorkItem next = Assert.IsType<SubscriptionDeliveryWorkItem>(
            await fixture.DeliveryQueue.ClaimNextAsync(CancellationToken.None));

        Assert.Equal(pendingDeliveryId, next.Id);
        Assert.Equal("dead_lettered", (await fixture.GetSubscriptionDeliveryAsync(exhaustedDeliveryId)).Status);
    }

    [Fact]
    public async Task Dispatch_WhenTransientFinalizationFails_RetriesDatabaseOnly()
    {
        Guid deliveryId = await fixture.FanoutSingleDeliveryAsync();
        await ExecuteSqlAsync(
            """
            CREATE SEQUENCE test_finalization_retry_sequence;
            CREATE FUNCTION test_fail_first_attempt_finalization() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF nextval('test_finalization_retry_sequence') = 1 THEN
                    RAISE EXCEPTION 'injected transient finalization failure' USING ERRCODE = '40001';
                END IF;
                RETURN NEW;
            END $$;
            CREATE TRIGGER test_fail_first_attempt_finalization
                BEFORE UPDATE ON delivery_attempts
                FOR EACH ROW
                WHEN (OLD.status = 'in_progress' AND NEW.status <> 'in_progress')
                EXECUTE FUNCTION test_fail_first_attempt_finalization();
            """);

        try
        {
            Assert.Equal(1, await fixture.RunDeliveryBatchAsync(1));
        }
        finally
        {
            await ExecuteSqlAsync(
                """
                DROP TRIGGER IF EXISTS test_fail_first_attempt_finalization ON delivery_attempts;
                DROP FUNCTION IF EXISTS test_fail_first_attempt_finalization();
                DROP SEQUENCE IF EXISTS test_finalization_retry_sequence;
                """);
        }

        Assert.Single(fixture.DeliveryClient.Calls);
        Assert.Equal("succeeded", (await fixture.GetSubscriptionDeliveryAsync(deliveryId)).Status);
        Assert.Equal("succeeded", Assert.Single(await fixture.GetDeliveryAttemptsAsync(deliveryId)).Status);
    }

    [Fact]
    public async Task Replay_ResetsRetryCycleButAppendsLifetimeAttemptHistory()
    {
        Guid eventId = await fixture.InsertEventAndOutboxAsync("payment.created");
        Assert.Equal(1, await fixture.RunFanoutBatchAsync());
        SubscriptionDeliveryState initial = Assert.Single(await fixture.GetSubscriptionDeliveriesAsync(eventId));

        for (int expectedAttempt = 1; expectedAttempt <= RetryPolicy.DefaultMaxAttempts; expectedAttempt++)
        {
            SubscriptionDeliveryWorkItem? claim = await fixture.DeliveryQueue.ClaimNextAsync(CancellationToken.None);
            Assert.NotNull(claim);
            SubscriptionDeliveryWorkItem claimed = claim;
            Assert.Equal(expectedAttempt, claimed.AttemptNumber);
            DeliveryFinalizationResult result = await fixture.DeliveryQueue.FinalizeAsync(FailedHttpCompletion(claimed), CancellationToken.None);
            Assert.Equal(DeliveryFinalizationStatus.Applied, result.Status);

            if (expectedAttempt < RetryPolicy.DefaultMaxAttempts)
                await fixture.ForceDeliveryRetryNowAsync(eventId);
        }

        Assert.True(await fixture.ReplayAsync(eventId));
        SubscriptionDeliveryState replayed = await fixture.GetSubscriptionDeliveryAsync(initial.Id);
        Assert.Equal(RetryPolicy.DefaultMaxAttempts, replayed.LifetimeAttemptCount);
        Assert.Equal(0, replayed.RetryCycleAttemptCount);

        SubscriptionDeliveryWorkItem? replayedClaim = await fixture.DeliveryQueue.ClaimNextAsync(CancellationToken.None);
        Assert.NotNull(replayedClaim);
        SubscriptionDeliveryWorkItem replayClaim = replayedClaim;
        Assert.Equal(4, replayClaim.AttemptNumber);

        IReadOnlyList<DeliveryAttemptState> attempts = await fixture.GetDeliveryAttemptsAsync(initial.Id);
        Assert.Equal([1, 2, 3, 4], attempts.Select(attempt => attempt.AttemptNumber));
        Assert.Equal("in_progress", attempts[^1].Status);

        SubscriptionDeliveryState activeReplay = await fixture.GetSubscriptionDeliveryAsync(initial.Id);
        Assert.Equal(4, activeReplay.LifetimeAttemptCount);
        Assert.Equal(1, activeReplay.RetryCycleAttemptCount);
    }

    private static DeliveryAttemptCompletion SuccessfulCompletion(SubscriptionDeliveryWorkItem claimed) => new(
        claimed.Id,
        claimed.AttemptId,
        true,
        null,
        claimed.PayloadJson,
        200,
        null,
        null);

    private static DeliveryAttemptCompletion FailedHttpCompletion(SubscriptionDeliveryWorkItem claimed) => new(
        claimed.Id,
        claimed.AttemptId,
        false,
        DeliveryFailurePhase.Http,
        claimed.PayloadJson,
        500,
        null,
        "HTTP 500");

    private async Task InstallRaceBarrierAsync(string blockedStatus, long advisoryLockKey)
    {
        await ExecuteSqlAsync(
            $$"""
            CREATE SEQUENCE test_delivery_race_sequence;
            CREATE FUNCTION test_block_delivery_attempt_update() RETURNS trigger LANGUAGE plpgsql AS $function$
            BEGIN
                PERFORM nextval('test_delivery_race_sequence');
                PERFORM pg_advisory_xact_lock({{advisoryLockKey}});
                RETURN NEW;
            END
            $function$;
            CREATE TRIGGER test_block_delivery_attempt_update
                BEFORE UPDATE ON delivery_attempts
                FOR EACH ROW
                WHEN (NEW.status = '{{blockedStatus}}')
                EXECUTE FUNCTION test_block_delivery_attempt_update();
            """);
    }

    private async Task WaitForRaceBarrierAsync()
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(5))
        {
            await using var connection = new NpgsqlConnection(fixture.ConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                "SELECT is_called FROM test_delivery_race_sequence",
                connection);
            if (await command.ExecuteScalarAsync() is true)
                return;

            await Task.Delay(TimeSpan.FromMilliseconds(10));
        }

        throw new TimeoutException("The delivery race did not reach its database barrier within five seconds.");
    }

    private async Task WaitForBlockedFinalizationAsync()
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(5))
        {
            await using var connection = new NpgsqlConnection(fixture.ConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                """
                SELECT EXISTS (
                    SELECT 1
                    FROM pg_stat_activity
                    WHERE datname = current_database()
                      AND wait_event_type = 'Lock'
                      AND query LIKE '%FROM subscription_deliveries%FOR UPDATE%'
                )
                """,
                connection);
            if (await command.ExecuteScalarAsync() is true)
                return;

            await Task.Delay(TimeSpan.FromMilliseconds(10));
        }

        throw new TimeoutException("The stale finalization did not block behind the reclaim transaction within five seconds.");
    }

    private async Task ExecuteSqlAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<object?> ExecuteScalarAsync(
        NpgsqlConnection connection,
        string sql,
        long advisoryLockKey)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("LockKey", advisoryLockKey);
        return await command.ExecuteScalarAsync();
    }
}
