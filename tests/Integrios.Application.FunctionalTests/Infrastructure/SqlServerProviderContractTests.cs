using System.Text.Json;
using Dapper;
using Integrios.Application.Connections;
using Integrios.Application.Delivery;
using Integrios.Application.Events;
using Integrios.Application.Integrations;
using Integrios.Application.Outbox;
using Integrios.Domain.Subscriptions;
using Integrios.Domain.Delivery;
using Integrios.Infrastructure;
using Integrios.Infrastructure.Connections;
using Integrios.Infrastructure.Data;
using Integrios.Infrastructure.Delivery;
using Integrios.Infrastructure.Events;
using Integrios.Infrastructure.Integrations;
using Integrios.Infrastructure.Outbox;
using Integrios.Infrastructure.Topics;
using Integrios.Application.FunctionalTests.Worker;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.MsSql;

namespace Integrios.Application.FunctionalTests.Infrastructure;

public sealed class SqlServerProviderFixture : IAsyncLifetime
{
    private readonly MsSqlContainer container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-CU20-ubuntu-22.04")
        .WithPassword("Integrios_Test_2026!")
        .Build();

    public string ConnectionString => container.GetConnectionString();

    public async Task InitializeAsync()
    {
        await container.StartAsync();
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Provider"] = "sqlserver",
                ["ConnectionStrings:SqlServer"] = ConnectionString,
            })
            .Build();
        using ServiceProvider provider = new ServiceCollection()
            .AddAdminInfrastructureServices(configuration)
            .BuildServiceProvider();
        await Task.WhenAll(provider.MigrateDatabaseAsync(), provider.MigrateDatabaseAsync());
        await provider.MigrateDatabaseAsync();
    }

    public Task DisposeAsync() => container.DisposeAsync().AsTask();

    internal IntegriosDbContext CreateContext() => new(CreateOptions());

    internal DbContextOptions<IntegriosDbContext> CreateOptions() =>
        new DbContextOptionsBuilder<IntegriosDbContext>()
            .UseSqlServer(ConnectionString, sql => sql.MigrationsAssembly("Integrios.Migrations.SqlServer"))
            .Options;
}

public sealed class SqlServerProviderContractTests(SqlServerProviderFixture fixture)
    : IClassFixture<SqlServerProviderFixture>
{
    [Fact]
    public async Task BaselineAndConsistencySeams_PreserveTheSharedContracts()
    {
        await using var connection = new SqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();

        string[] jsonTypes = (await connection.QueryAsync<string>(
            """
            SELECT TYPE_NAME(c.user_type_id)
            FROM sys.columns c
            JOIN sys.tables t ON t.object_id=c.object_id
            WHERE t.name=N'integrations' AND c.name=N'manifest'
            UNION ALL
            SELECT TYPE_NAME(c.user_type_id)
            FROM sys.columns c
            JOIN sys.tables t ON t.object_id=c.object_id
            WHERE t.name=N'events' AND c.name=N'payload'
            """)).ToArray();
        Assert.Equal(["nvarchar", "nvarchar"], jsonTypes);
        Assert.Equal(2, await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM sys.triggers WHERE name IN (N'integrations_reject_functional_update', N'events_require_active_topic_source')"));

        Guid tenantId = Guid.NewGuid();
        Guid integrationId = Guid.NewGuid();
        Guid connectionId = Guid.NewGuid();
        string manifestJson = Manifest("SQL Server Source").GetRawText();
        await connection.ExecuteAsync(
            """
            INSERT INTO tenants (id, slug, name, status, created_at, updated_at)
            VALUES (@TenantId, N'sql-server', N'SQL Server', N'active', SYSUTCDATETIME(), SYSUTCDATETIME());
            INSERT INTO integrations (id, [key], contract_version, manifest_schema_version, name, direction,
                supported_auth_schemes, status, created_at, updated_at, manifest)
            VALUES (@IntegrationId, N'test_http', 1, 1, N'SQL Server Source', N'both', N'[]', N'active',
                SYSUTCDATETIME(), SYSUTCDATETIME(), @ManifestJson);
            INSERT INTO connections (id, tenant_id, integration_id, name, config, status, created_at, updated_at)
            VALUES (@ConnectionId, @TenantId, @IntegrationId, N'sql-server-connection',
                N'{"base_uri":"https://example.test"}', N'active', SYSUTCDATETIME(), SYSUTCDATETIME());
            """,
            new { TenantId = tenantId, IntegrationId = integrationId, ConnectionId = connectionId, ManifestJson = manifestJson });
        SqlException manifestArray = await Assert.ThrowsAsync<SqlException>(() => connection.ExecuteAsync(
            """
            INSERT INTO integrations (id, [key], contract_version, manifest_schema_version, name, direction,
                supported_auth_schemes, status, created_at, updated_at, manifest)
            VALUES (NEWID(), N'bad_json', 1, 1, N'Bad JSON', N'source', N'[]', N'active',
                SYSUTCDATETIME(), SYSUTCDATETIME(), N'[]');
            """));
        Assert.Equal(547, manifestArray.Number);
        SqlException envelopeArray = await Assert.ThrowsAsync<SqlException>(() => connection.ExecuteAsync(
            "UPDATE connections SET source_verification=N'[]' WHERE id=@ConnectionId",
            new { ConnectionId = connectionId }));
        Assert.Equal(547, envelopeArray.Number);
        SqlException malformedConfig = await Assert.ThrowsAsync<SqlException>(() => connection.ExecuteAsync(
            "UPDATE connections SET config=N'not-json' WHERE id=@ConnectionId",
            new { ConnectionId = connectionId }));
        Assert.Equal(547, malformedConfig.Number);

        Guid topicId;
        await using (IntegriosDbContext context = fixture.CreateContext())
        {
            var repository = new TopicRepository(context);
            var topic = await repository.CreateAsync(
                tenantId, "payments", null, [connectionId], CancellationToken.None);
            topicId = topic.Id;
            Assert.Single(topic.Sources);
            Assert.NotNull(topic.Sources[0].Endpoint);
        }

        Guid subscriptionId = Guid.NewGuid();
        await connection.ExecuteAsync(
            """
            INSERT INTO subscriptions (id, topic_id, tenant_id, name, match_rules, destination_connection_id,
                http_delivery, status, order_index, created_at, updated_at)
            VALUES (@SubscriptionId, @TopicId, @TenantId, N'payments-http', N'{"event_type":"payment.created"}',
                @ConnectionId, N'{"body":"json","method":"POST","headers":{},"version":1}',
                N'active', 0, SYSUTCDATETIME(), SYSUTCDATETIME());
            """,
            new { SubscriptionId = subscriptionId, TopicId = topicId, TenantId = tenantId, ConnectionId = connectionId });

        var factory = new PooledDbContextFactory<IntegriosDbContext>(fixture.CreateOptions());
        var acceptance = new SqlServerEventAcceptance(factory);
        var submission = new EventSubmission
        {
            TenantId = tenantId,
            TopicId = topicId,
            SourceConnectionId = connectionId,
            EventType = "payment.created",
            Payload = Json("""{"amount":42}"""),
            IdempotencyKey = "sqlserver-idempotency"
        };
        EventAcceptance[] accepted = await Task.WhenAll(
            acceptance.AcceptAsync(submission, null, CancellationToken.None),
            acceptance.AcceptAsync(submission, null, CancellationToken.None));
        Assert.Single(accepted, result => result.AlreadyAccepted);
        Assert.Single(accepted, result => !result.AlreadyAccepted);

        await connection.ExecuteAsync(
            "UPDATE topic_sources SET status=N'inactive', inactive_at=SYSUTCDATETIME() WHERE tenant_id=@TenantId AND topic_id=@TopicId AND connection_id=@ConnectionId",
            new { TenantId = tenantId, TopicId = topicId, ConnectionId = connectionId });

        var fanout = new SqlServerOutboxFanout(factory);
        var fanoutResults = await Task.WhenAll(
            fanout.ProcessNextAsync(CancellationToken.None),
            fanout.ProcessNextAsync(CancellationToken.None));
        await ConsistencyContractAssertions.FanoutProcessesOnceAsync(
            accepted[0].EventId,
            fanoutResults.Select(result => result is null ? 0 : 1).ToArray(),
            eventId => connection.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM subscription_deliveries WHERE event_id=@EventId", new { EventId = eventId }),
            eventId => connection.ExecuteScalarAsync<bool>(
                "SELECT CASE WHEN processed_at IS NULL THEN CAST(0 AS bit) ELSE CAST(1 AS bit) END FROM outbox WHERE event_id=@EventId",
                new { EventId = eventId }),
            eventId => connection.ExecuteScalarAsync<string>(
                "SELECT status FROM events WHERE id=@EventId", new { EventId = eventId }));
        await Assert.ThrowsAsync<EventAcceptanceException>(() => acceptance.AcceptAsync(
            submission with { IdempotencyKey = "retired-source" }, null, CancellationToken.None));

        var queue = new SubscriptionDeliveryQueue(
            new SqlServerConnectionFactory(fixture.ConnectionString),
            DeliveryExecutionOptions.Default,
            new DeliveryOutcomePolicy(new RetryPolicy()));
        SubscriptionDeliveryWorkItem work = await queue.ClaimNextAsync(CancellationToken.None)
            ?? throw new InvalidOperationException("SQL Server did not claim the delivery.");
        DeliveryFinalizationResult finalization = await queue.FinalizeAsync(
            new DeliveryAttemptCompletion(work.Id, work.AttemptId, true, null, work.PayloadJson, 200, null, null),
            CancellationToken.None);
        Assert.Equal(DeliveryFinalizationStatus.Applied, finalization.Status);

        await connection.ExecuteAsync(
            "UPDATE topic_sources SET status=N'active', inactive_at=NULL WHERE tenant_id=@TenantId AND topic_id=@TopicId AND connection_id=@ConnectionId",
            new { TenantId = tenantId, TopicId = topicId, ConnectionId = connectionId });

        await acceptance.AcceptAsync(
            submission with { IdempotencyKey = "claim-rollback" }, null, CancellationToken.None);
        OutboxFanoutResult claimRollbackFanout = await fanout.ProcessNextAsync(CancellationToken.None)
            ?? throw new InvalidOperationException("SQL Server did not fan out the claim rollback event.");
        Guid claimRollbackDeliveryId = await GetDeliveryIdAsync(claimRollbackFanout.EventId);
        await connection.ExecuteAsync(
            """
            CREATE TRIGGER test_fail_delivery_claim ON subscription_deliveries AFTER UPDATE AS
            BEGIN
                IF EXISTS (SELECT 1 FROM inserted i JOIN deleted d ON i.id=d.id
                    WHERE d.status=N'pending' AND i.status=N'in_flight')
                    THROW 51004, 'injected claim failure', 1;
            END
            """);
        try
        {
            await Assert.ThrowsAsync<SqlException>(() => queue.ClaimNextAsync(CancellationToken.None));
        }
        finally
        {
            await connection.ExecuteAsync("DROP TRIGGER IF EXISTS test_fail_delivery_claim");
        }
        await ConsistencyContractAssertions.ClaimFailureRollsBackAsync(
            claimRollbackDeliveryId, GetDeliveryAsync, GetAttemptsAsync);
        SubscriptionDeliveryWorkItem claimRollbackWork = await queue.ClaimNextAsync(CancellationToken.None)
            ?? throw new InvalidOperationException("SQL Server did not reclaim the rolled-back delivery.");
        await queue.FinalizeAsync(SuccessfulCompletion(claimRollbackWork), CancellationToken.None);

        await acceptance.AcceptAsync(
            submission with { IdempotencyKey = "finalization-rollback" }, null, CancellationToken.None);
        OutboxFanoutResult finalizationRollbackFanout = await fanout.ProcessNextAsync(CancellationToken.None)
            ?? throw new InvalidOperationException("SQL Server did not fan out the finalization rollback event.");
        Guid finalizationRollbackDeliveryId = await GetDeliveryIdAsync(finalizationRollbackFanout.EventId);
        SubscriptionDeliveryWorkItem finalizationRollbackWork = await queue.ClaimNextAsync(CancellationToken.None)
            ?? throw new InvalidOperationException("SQL Server did not claim the finalization rollback delivery.");
        await connection.ExecuteAsync(
            """
            CREATE TRIGGER test_fail_delivery_finalization ON subscription_deliveries AFTER UPDATE AS
            BEGIN
                IF EXISTS (SELECT 1 FROM inserted i JOIN deleted d ON i.id=d.id
                    WHERE d.status=N'in_flight' AND i.status<>N'in_flight')
                    THROW 51005, 'injected finalization failure', 1;
            END
            """);
        try
        {
            await Assert.ThrowsAsync<SqlException>(() =>
                queue.FinalizeAsync(SuccessfulCompletion(finalizationRollbackWork), CancellationToken.None));
        }
        finally
        {
            await connection.ExecuteAsync("DROP TRIGGER IF EXISTS test_fail_delivery_finalization");
        }
        await ConsistencyContractAssertions.FinalizationFailureRollsBackAsync(
            finalizationRollbackDeliveryId,
            finalizationRollbackWork.AttemptId,
            GetDeliveryAsync,
            GetAttemptsAsync);
        await queue.FinalizeAsync(SuccessfulCompletion(finalizationRollbackWork), CancellationToken.None);

        var authoringLock = new SqlServerConnectionAuthoringLock(factory);
        await using (await authoringLock.AcquireAsync([connectionId], CancellationToken.None))
        {
            await Assert.ThrowsAsync<ConnectionAuthoringConflictException>(
                () => authoringLock.AcquireAsync([connectionId], CancellationToken.None));
        }

        await using (IntegriosDbContext context = fixture.CreateContext())
        {
            SqlException immutable = await Assert.ThrowsAsync<SqlException>(() =>
                context.Database.ExecuteSqlRawAsync("UPDATE integrations SET direction=N'destination' WHERE id={0}", integrationId));
            Assert.Equal(51000, immutable.Number);

            var manifestStore = new SqlServerIntegrationManifestStore(context);
            var updated = IntegrationManifestParser.DeserializeStored(Manifest("Renamed Source").GetRawText());
            IntegrationManifestStoreResult result = await manifestStore.ApplyAsync(
                updated, IntegrationManifestApplyAuthority.Operator, CancellationToken.None);
            Assert.Equal(IntegrationManifestApplyOutcome.PresentationReconciled, result.Outcome);
            Assert.Equal("Renamed Source", result.Integration.Name);
        }

        EventAcceptance lockedEvent = await acceptance.AcceptAsync(
            submission with { IdempotencyKey = "locked-row" }, null, CancellationToken.None);
        await using (var ownerConnection = new SqlConnection(fixture.ConnectionString))
        {
            await ownerConnection.OpenAsync();
            await using var ownerTransaction = await ownerConnection.BeginTransactionAsync();
            Assert.NotNull(await ownerConnection.ExecuteScalarAsync<Guid?>(
                "SELECT id FROM outbox WITH (UPDLOCK, ROWLOCK) WHERE event_id=@EventId",
                new { lockedEvent.EventId }, ownerTransaction));
            Assert.Null(await fanout.ProcessNextAsync(CancellationToken.None));
            await ownerTransaction.RollbackAsync();
        }
        Assert.NotNull(await fanout.ProcessNextAsync(CancellationToken.None));

        EventAcceptance rollbackEvent = await acceptance.AcceptAsync(
            submission with { IdempotencyKey = "rollback-reclaim" }, null, CancellationToken.None);
        await connection.ExecuteAsync(
            """
            CREATE TRIGGER fail_outbox_completion ON outbox AFTER UPDATE AS
            BEGIN
                IF UPDATE(processed_at) THROW 51002, 'simulated completion failure', 1;
            END
            """);
        try
        {
            await Assert.ThrowsAsync<SqlException>(() => fanout.ProcessNextAsync(CancellationToken.None));
            Assert.Equal("accepted", await connection.ExecuteScalarAsync<string>(
                "SELECT status FROM events WHERE id=@EventId", new { rollbackEvent.EventId }));
            Assert.Equal(0, await connection.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM subscription_deliveries WHERE event_id=@EventId", new { rollbackEvent.EventId }));
        }
        finally
        {
            await connection.ExecuteAsync("DROP TRIGGER IF EXISTS fail_outbox_completion");
        }
        Assert.NotNull(await fanout.ProcessNextAsync(CancellationToken.None));

        EventAcceptance scalarEvent = await acceptance.AcceptAsync(
            submission with { IdempotencyKey = "scalar-json", Payload = Json("42") },
            null,
            CancellationToken.None);
        Assert.NotNull(await fanout.ProcessNextAsync(CancellationToken.None));
        Assert.Equal("42", await connection.ExecuteScalarAsync<string>(
            "SELECT payload FROM events WHERE id=@EventId", new { scalarEvent.EventId }));

        const int stressCount = 30;
        for (int index = 0; index < stressCount; index++)
        {
            await acceptance.AcceptAsync(
                submission with { IdempotencyKey = $"stress-{index}" }, null, CancellationToken.None);
        }
        int[] fannedOut = await Task.WhenAll(DrainFanoutAsync(), DrainFanoutAsync());
        Assert.Equal(stressCount, fannedOut.Sum());

        int[] delivered = await Task.WhenAll(DrainDeliveriesAsync(), DrainDeliveriesAsync());
        Assert.Equal(stressCount + 3, delivered.Sum());
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM (SELECT event_id, subscription_id FROM subscription_deliveries GROUP BY event_id, subscription_id HAVING COUNT(*) > 1) d"));

        await acceptance.AcceptAsync(
            submission with { IdempotencyKey = "expired-lease" }, null, CancellationToken.None);
        await fanout.ProcessNextAsync(CancellationToken.None);
        SubscriptionDeliveryWorkItem stale = await queue.ClaimNextAsync(CancellationToken.None)
            ?? throw new InvalidOperationException("SQL Server did not claim the stale delivery.");
        await connection.ExecuteAsync(
            "UPDATE subscription_deliveries SET lease_expires_at=DATEADD(second, -1, SYSUTCDATETIME()) WHERE id=@Id",
            new { stale.Id });
        var reclaimed = Assert.IsType<ClaimedSubscriptionDelivery>(
            await queue.ClaimNextWithRecoveryAsync(CancellationToken.None));
        Assert.NotEqual(stale.AttemptId, reclaimed.WorkItem.AttemptId);
        Assert.Equal(DeliveryFinalizationStatus.OwnershipLost, (await queue.FinalizeAsync(
            new DeliveryAttemptCompletion(stale.Id, stale.AttemptId, true, null, stale.PayloadJson, 200, null, null),
            CancellationToken.None)).Status);
        Assert.Equal(DeliveryFinalizationStatus.Applied, (await queue.FinalizeAsync(
            new DeliveryAttemptCompletion(reclaimed.WorkItem.Id, reclaimed.WorkItem.AttemptId, true, null,
                reclaimed.WorkItem.PayloadJson, 200, null, null), CancellationToken.None)).Status);

        await RunLeaseRaceAsync(finalizationWins: true);
        await RunLeaseRaceAsync(finalizationWins: false);

        EventAcceptance deadLetterEvent = await acceptance.AcceptAsync(
            submission with { IdempotencyKey = "dead-letter-budget" }, null, CancellationToken.None);
        await fanout.ProcessNextAsync(CancellationToken.None);
        Guid deadLetterDeliveryId = Guid.Empty;
        for (int attempt = 0; attempt < RetryPolicy.DefaultMaxAttempts; attempt++)
        {
            SubscriptionDeliveryWorkItem failed = await queue.ClaimNextAsync(CancellationToken.None)
                ?? throw new InvalidOperationException("SQL Server did not claim the retry delivery.");
            deadLetterDeliveryId = failed.Id;
            await queue.FinalizeAsync(
                new DeliveryAttemptCompletion(failed.Id, failed.AttemptId, false, DeliveryFailurePhase.Http,
                    failed.PayloadJson, 500, null, "failed", RetryAfter: TimeSpan.Zero),
                CancellationToken.None);
            if (attempt < RetryPolicy.DefaultMaxAttempts - 1)
            {
                await connection.ExecuteAsync(
                    "UPDATE subscription_deliveries SET deliver_after=DATEADD(second, -1, SYSUTCDATETIME()) WHERE id=@Id",
                    new { failed.Id });
            }
        }
        Assert.Equal("dead_lettered", await connection.ExecuteScalarAsync<string>(
            "SELECT status FROM subscription_deliveries WHERE id=@Id", new { Id = deadLetterDeliveryId }));
        Assert.True(await new DeadLetterReplay(new SqlServerConnectionFactory(fixture.ConnectionString))
            .ReplayDeadLetteredAsync(tenantId, deadLetterEvent.EventId, CancellationToken.None));
        Assert.Equal("pending", await connection.ExecuteScalarAsync<string>(
            "SELECT status FROM subscription_deliveries WHERE id=@Id", new { Id = deadLetterDeliveryId }));

        async Task<int> DrainFanoutAsync()
        {
            int count = 0;
            while (await fanout.ProcessNextAsync(CancellationToken.None) is not null)
                count++;
            return count;
        }

        async Task<int> DrainDeliveriesAsync()
        {
            int count = 0;
            while (await queue.ClaimNextAsync(CancellationToken.None) is { } item)
            {
                DeliveryFinalizationResult result = await queue.FinalizeAsync(
                    new DeliveryAttemptCompletion(item.Id, item.AttemptId, true, null, item.PayloadJson, 200, null, null),
                    CancellationToken.None);
                Assert.Equal(DeliveryFinalizationStatus.Applied, result.Status);
                count++;
            }
            return count;
        }

        async Task RunLeaseRaceAsync(bool finalizationWins)
        {
            string suffix = finalizationWins ? "finalize" : "reclaim";
            await acceptance.AcceptAsync(
                submission with { IdempotencyKey = $"lease-race-{suffix}" }, null, CancellationToken.None);
            await fanout.ProcessNextAsync(CancellationToken.None);
            SubscriptionDeliveryWorkItem first = await queue.ClaimNextAsync(CancellationToken.None)
                ?? throw new InvalidOperationException("SQL Server did not claim the race delivery.");
            await connection.ExecuteAsync(
                "UPDATE subscription_deliveries SET lease_expires_at=DATEADD(second, -1, SYSUTCDATETIME()) WHERE id=@Id",
                new { first.Id });

            string blockedStatus = finalizationWins ? "succeeded" : "indeterminate";
            string resource = $"delivery-race-{suffix}";
            await connection.ExecuteAsync("CREATE TABLE ##delivery_race_signal (hit bit NOT NULL)");
            await connection.ExecuteAsync($"""
                CREATE TRIGGER test_block_delivery_attempt_update ON delivery_attempts AFTER UPDATE AS
                BEGIN
                    IF EXISTS (SELECT 1 FROM inserted WHERE status=N'{blockedStatus}')
                    BEGIN
                        INSERT INTO ##delivery_race_signal VALUES (1);
                        DECLARE @result int;
                        EXEC @result=sp_getapplock @Resource=N'{resource}', @LockMode='Exclusive',
                            @LockOwner='Transaction', @LockTimeout=10000;
                        IF @result < 0 THROW 51003, 'race barrier timed out', 1;
                    END
                END
                """);

            await using var barrier = new SqlConnection(fixture.ConnectionString);
            await barrier.OpenAsync();
            await barrier.ExecuteAsync(
                "DECLARE @r int; EXEC @r=sp_getapplock @Resource=@Resource, @LockMode='Exclusive', @LockOwner='Session';",
                new { Resource = resource });
            bool barrierHeld = true;
            Task<DeliveryFinalizationResult>? finalizationTask = null;
            Task<SubscriptionDeliveryClaimResult?>? reclaimTask = null;
            try
            {
                if (finalizationWins)
                {
                    finalizationTask = queue.FinalizeAsync(
                        new DeliveryAttemptCompletion(first.Id, first.AttemptId, true, null, first.PayloadJson, 200, null, null),
                        CancellationToken.None);
                }
                else
                {
                    reclaimTask = queue.ClaimNextWithRecoveryAsync(CancellationToken.None);
                }

                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                while (await connection.ExecuteScalarAsync<int>(
                           "SELECT COUNT(*) FROM ##delivery_race_signal WITH (NOLOCK)") == 0)
                    await Task.Delay(20, timeout.Token);

                if (finalizationWins)
                {
                    reclaimTask = queue.ClaimNextWithRecoveryAsync(CancellationToken.None);
                    Assert.Null(await reclaimTask);
                }
                else
                {
                    finalizationTask = queue.FinalizeAsync(
                        new DeliveryAttemptCompletion(first.Id, first.AttemptId, true, null, first.PayloadJson, 200, null, null),
                        CancellationToken.None);
                    await Task.Delay(100);
                    Assert.False(finalizationTask.IsCompleted);
                }

                await barrier.ExecuteAsync(
                    "EXEC sp_releaseapplock @Resource=@Resource, @LockOwner='Session'",
                    new { Resource = resource });
                barrierHeld = false;
                DeliveryFinalizationResult finalized = await finalizationTask!;
                SubscriptionDeliveryClaimResult? claimed = await reclaimTask!;
                Assert.Equal(
                    finalizationWins ? DeliveryFinalizationStatus.Applied : DeliveryFinalizationStatus.OwnershipLost,
                    finalized.Status);
                if (finalizationWins)
                    Assert.Null(claimed);
                else
                {
                    var active = Assert.IsType<ClaimedSubscriptionDelivery>(claimed).WorkItem;
                    Assert.Equal(DeliveryFinalizationStatus.Applied, (await queue.FinalizeAsync(
                        new DeliveryAttemptCompletion(active.Id, active.AttemptId, true, null,
                            active.PayloadJson, 200, null, null), CancellationToken.None)).Status);
                }
            }
            finally
            {
                if (barrierHeld)
                {
                    await barrier.ExecuteAsync(
                        "EXEC sp_releaseapplock @Resource=@Resource, @LockOwner='Session'",
                        new { Resource = resource });
                }
                if (finalizationTask is not null)
                    await finalizationTask;
                if (reclaimTask is not null)
                    await reclaimTask;
                await connection.ExecuteAsync("DROP TRIGGER IF EXISTS test_block_delivery_attempt_update; DROP TABLE IF EXISTS ##delivery_race_signal;");
            }
        }

        Task<Guid> GetDeliveryIdAsync(Guid eventId) => connection.ExecuteScalarAsync<Guid>(
            "SELECT id FROM subscription_deliveries WHERE event_id=@EventId", new { EventId = eventId });

        Task<SubscriptionDeliveryState> GetDeliveryAsync(Guid deliveryId) =>
            connection.QuerySingleAsync<SubscriptionDeliveryState>(
                """
                SELECT id AS Id, subscription_id AS SubscriptionId, status AS Status,
                    lifetime_attempt_count AS LifetimeAttemptCount,
                    retry_cycle_attempt_count AS RetryCycleAttemptCount,
                    active_attempt_id AS ActiveAttemptId, lease_expires_at AS LeaseExpiresAt,
                    deliver_after AS DeliverAfter
                FROM subscription_deliveries WHERE id=@DeliveryId
                """,
                new { DeliveryId = deliveryId });

        async Task<IReadOnlyList<DeliveryAttemptState>> GetAttemptsAsync(Guid deliveryId) =>
            (await connection.QueryAsync<DeliveryAttemptState>(
                """
                SELECT id AS Id, subscription_delivery_id AS SubscriptionDeliveryId,
                    attempt_number AS AttemptNumber, status AS Status, failure_phase AS FailurePhase,
                    completed_at AS CompletedAt
                FROM delivery_attempts WHERE subscription_delivery_id=@DeliveryId ORDER BY attempt_number
                """,
                new { DeliveryId = deliveryId })).AsList();

        static DeliveryAttemptCompletion SuccessfulCompletion(SubscriptionDeliveryWorkItem item) =>
            new(item.Id, item.AttemptId, true, null, item.PayloadJson, 200, null, null);
    }

    private static JsonElement Manifest(string name) => Json($$$"""
        {
          "manifest_schema_version":1,
          "key":"test_http",
          "contract_version":1,
          "direction":"both",
          "source_verification":{"allow_unverified":true,"schemes":[]},
          "destination_authentication":{"allow_unauthenticated":true,"schemes":[]},
          "source_adapter":{"key":"verified_webhook","contract_version":1,"config":{}},
          "presentation":{"name":"{{{name}}}","event_types":[],"authoring_presets":[]}
        }
        """);

    private static JsonElement Json(string value) => JsonSerializer.Deserialize<JsonElement>(value);
}
