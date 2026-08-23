using System.Data.Common;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Integrios.Application;
using Integrios.Application.Auth;
using Integrios.Application.Delivery;
using Integrios.Application.Recovery;
using Integrios.Application.Events;
using Integrios.Application.Outbox;
using Integrios.Application.Secrets;
using Integrios.Application.Subscriptions;
using Integrios.Application.Transforms;
using Integrios.Domain.Subscriptions;
using Integrios.Infrastructure;
using Integrios.Infrastructure.Auth;
using Integrios.Infrastructure.Data;
using Integrios.Infrastructure.Delivery;
using Integrios.Infrastructure.Events;
using Integrios.Infrastructure.Subscriptions;
using Integrios.Infrastructure.Transforms;
using Integrios.Tests.Shared;
using MediatR;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Respawn;

namespace Integrios.Application.FunctionalTests.Worker;

public sealed class WorkerRoutingFixture : IAsyncLifetime
{
    public const string TenantToken = "intg_f0f0e1e1d2d2c3c3aabbccddeeff00112233445566778899aabbccddeeff0011";
    public const string LedgerSinkUrl = "http://test-sink/ledger";
    public const string RiskSinkUrl = "http://test-sink/risk";

    private static readonly Guid HttpConnectorId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid TenantId = Guid.Parse("cccccccc-0000-0000-0000-000000000001");
    private static readonly Guid OrphanTenantId = Guid.Parse("cccccccc-0000-0000-0000-000000000009");
    private static readonly Guid SourceConnectionId = Guid.Parse("cccccccc-0000-0000-0000-000000000002");
    private static readonly Guid OrphanSourceConnectionId = Guid.Parse("cccccccc-0000-0000-0000-000000000008");
    private static readonly Guid LedgerConnectionId = Guid.Parse("cccccccc-0000-0000-0000-000000000003");
    private static readonly Guid RiskConnectionId = Guid.Parse("cccccccc-0000-0000-0000-000000000004");
    private static readonly Guid TopicId = Guid.Parse("cccccccc-0000-0000-0000-000000000005");

    private readonly FunctionalDatabase database = new();
    private Respawner respawner = null!;
    private ServiceProvider infrastructureProvider = null!;
    private ServiceProvider applicationProvider = null!;
    private IntegriosDbContext dbContext = null!;
    private IDeadLetterReplay deadLetterReplay = null!;
    private ISubscriptionRepository subscriptionRepository = null!;
    private ITenantEventLookup eventLookup = null!;
    private IMediator mediator = null!;

    public FakeDeliveryClient DeliveryClient { get; } = new();
    public MutableSecretResolver SecretResolver { get; } = new();
    private string ConnectionString => database.ConnectionString;
    internal SubscriptionDeliveryQueue DeliveryQueue { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await database.StartAsync();
        respawner = await database.CreateRespawnerAsync();

        infrastructureProvider = new ServiceCollection()
            .AddWorkerInfrastructureServices(database.Configuration)
            .BuildServiceProvider();
        var connectionFactory = infrastructureProvider.GetRequiredService<IDbConnectionFactory>();
        var outboxFanout = infrastructureProvider.GetRequiredService<IOutboxFanout>();
        DeliveryQueue = (SubscriptionDeliveryQueue)infrastructureProvider.GetRequiredService<ISubscriptionDeliveryQueue>();
        dbContext = new IntegriosDbContext(database.CreateOptions());
        deadLetterReplay = new DeadLetterReplay(connectionFactory);
        subscriptionRepository = new SubscriptionRepository(dbContext);
        eventLookup = new TenantEventLookup(connectionFactory);

        var services = new ServiceCollection();
        services.AddApplicationServices();
        services.AddSingleton(outboxFanout);
        services.AddSingleton<IDeadLetterReplay>(deadLetterReplay);
        services.AddSingleton(subscriptionRepository);
        services.AddSingleton<ISubscriptionRepository>(subscriptionRepository);
        services.AddSingleton(infrastructureProvider.GetRequiredService<DeliveryExecutionOptions>());
        services.AddSingleton<ISubscriptionDeliveryQueue>(DeliveryQueue);
        services.AddSingleton<IDeliveryClient>(_ => DeliveryClient);
        services.AddSingleton<IAuthSchemeHandler, ApiKeyHeaderAuthSchemeHandler>();
        services.AddSingleton<IAuthSchemeHandler, BearerTokenAuthSchemeHandler>();
        services.AddSingleton<IAuthSchemeRegistry, AuthSchemeRegistry>();
        services.AddSingleton<IDestinationAuthenticationSecretResolver>(_ => SecretResolver);
        services.AddSingleton<ITransformEvaluator, JsonataTransformEvaluator>();
        services.AddLogging();
        applicationProvider = services.BuildServiceProvider();
        mediator = applicationProvider.GetRequiredService<IMediator>();
    }

    public async Task DisposeAsync()
    {
        await applicationProvider.DisposeAsync();
        await dbContext.DisposeAsync();
        await infrastructureProvider.DisposeAsync();
        await database.DisposeAsync();
    }

    public async Task ResetAsync()
    {
        DeliveryClient.Reset();
        SecretResolver.Reset();
        dbContext.ChangeTracker.Clear();
        await using DbConnection connection = database.CreateConnection();
        await connection.OpenAsync();
        await respawner.ResetAsync(connection);
        await SeedRoutingDataAsync(connection);
    }

    public async Task<int> RunWorkerBatchAsync()
    {
        await mediator.Send(new ProcessOutboxBatchCommand(10));
        return await mediator.Send(new DispatchSubscriptionDeliveriesCommand(25));
    }

    public Task<int> RunFanoutBatchAsync(int batchSize = 10) => mediator.Send(new ProcessOutboxBatchCommand(batchSize));
    public Task<int> RunDeliveryBatchAsync(int batchSize = 25) => mediator.Send(new DispatchSubscriptionDeliveriesCommand(batchSize));

    public async Task<IReadOnlyList<SubscriptionDeliveryState>> GetSubscriptionDeliveriesAsync(Guid eventId) =>
        (await QueryAsync<DeliveryRow>(
            """
            SELECT id AS Id, subscription_id AS SubscriptionId, status AS Status,
                lifetime_attempt_count AS LifetimeAttemptCount, retry_cycle_attempt_count AS RetryCycleAttemptCount,
                active_attempt_id AS ActiveAttemptId, lease_expires_at AS LeaseExpiresAt, deliver_after AS DeliverAfter
            FROM subscription_deliveries WHERE event_id=@EventId ORDER BY created_at
            """, new { EventId = eventId })).Select(ToState).ToList();

    public async Task<Guid> FanoutSingleDeliveryAsync(string eventType = "payment.created")
    {
        Guid eventId = await InsertEventAndOutboxAsync(eventType);
        int processed = await RunFanoutBatchAsync();
        if (processed != 1) throw new InvalidOperationException($"Expected one Event to fan out, but processed {processed}.");
        return Assert.Single(await GetSubscriptionDeliveriesAsync(eventId)).Id;
    }

    public async Task ForceLeaseExpiredAsync(Guid deliveryId)
    {
        int updated = await ExecuteAsync(
            $"UPDATE subscription_deliveries SET lease_expires_at={database.OneSecondAgo} WHERE id=@DeliveryId AND status='in_flight'",
            new { DeliveryId = deliveryId });
        if (updated != 1) throw new InvalidOperationException($"Delivery {deliveryId} did not have an active lease to expire.");
    }

    public async Task<IReadOnlyList<DeliveryAttemptState>> GetDeliveryAttemptsAsync(Guid deliveryId) =>
        (await QueryAsync<AttemptRow>(
            """
            SELECT id AS Id, subscription_delivery_id AS SubscriptionDeliveryId, attempt_number AS AttemptNumber,
                status AS Status, failure_phase AS FailurePhase, completed_at AS CompletedAt
            FROM delivery_attempts WHERE subscription_delivery_id=@DeliveryId ORDER BY attempt_number
            """, new { DeliveryId = deliveryId })).Select(row => new DeliveryAttemptState(
                row.Id, row.SubscriptionDeliveryId, row.AttemptNumber, row.Status, row.FailurePhase, Offset(row.CompletedAt))).ToList();

    public async Task<SubscriptionDeliveryState> GetSubscriptionDeliveryAsync(Guid deliveryId)
    {
        DeliveryRow? row = (await QueryAsync<DeliveryRow>(
            """
            SELECT id AS Id, subscription_id AS SubscriptionId, status AS Status,
                lifetime_attempt_count AS LifetimeAttemptCount, retry_cycle_attempt_count AS RetryCycleAttemptCount,
                active_attempt_id AS ActiveAttemptId, lease_expires_at AS LeaseExpiresAt, deliver_after AS DeliverAfter
            FROM subscription_deliveries WHERE id=@DeliveryId
            """, new { DeliveryId = deliveryId })).SingleOrDefault();
        return row is null ? throw new InvalidOperationException($"No SubscriptionDelivery exists with id {deliveryId}.") : ToState(row);
    }

    public Task<int> GetSubscriptionDeliveryCountAsync(Guid eventId) =>
        ScalarAsync<int>("SELECT COUNT(*) FROM subscription_deliveries WHERE event_id=@EventId", new { EventId = eventId });

    public Task ForceDeliveryRetryNowAsync(Guid eventId) => ExecuteAsync(
        $"UPDATE subscription_deliveries SET deliver_after={database.OneSecondAgo} WHERE event_id=@EventId AND status='pending'",
        new { EventId = eventId });

    public async Task<Guid> InsertEventAndOutboxAsync(string eventType)
    {
        Guid eventId = Guid.NewGuid();
        await InsertEventRowAsync(eventId, TenantId, SourceConnectionId, eventType, TopicId);
        return eventId;
    }

    public async Task<Guid> InsertOrphanEventAndOutboxAsync(string eventType)
    {
        Guid eventId = Guid.NewGuid();
        await InsertEventRowAsync(eventId, OrphanTenantId, OrphanSourceConnectionId, eventType, null);
        return eventId;
    }

    public Task<string?> GetEventStatusAsync(Guid eventId) =>
        ScalarAsync<string?>("SELECT status FROM events WHERE id=@Id", new { Id = eventId });

    public async Task<bool> IsOutboxRowProcessedAsync(Guid eventId) =>
        await ScalarAsync<int>(
            "SELECT CASE WHEN processed_at IS NOT NULL THEN 1 ELSE 0 END FROM outbox WHERE event_id=@EventId",
            new { EventId = eventId }) == 1;

    public async Task<(int AttemptCount, DateTimeOffset? DeliverAfter)> GetOutboxRetryStateAsync(Guid eventId)
    {
        OutboxRetryRow row = (await QueryAsync<OutboxRetryRow>(
            "SELECT attempt_count AS AttemptCount, deliver_after AS DeliverAfter FROM outbox WHERE event_id=@EventId",
            new { EventId = eventId })).SingleOrDefault()
            ?? throw new InvalidOperationException($"No outbox row for event {eventId}");
        return (row.AttemptCount, Offset(row.DeliverAfter));
    }

    public Task ForceRetryNowAsync(Guid eventId) => ExecuteAsync(
        $"UPDATE outbox SET deliver_after={database.OneSecondAgo} WHERE event_id=@EventId", new { EventId = eventId });

    public Task SetSubscriptionTransformByNameAsync(string subscriptionName, string? transformConfigJson) => ExecuteAsync(
        $"UPDATE subscriptions SET transform_config={database.Json("@Config")} WHERE name=@Name",
        new { Config = transformConfigJson, Name = subscriptionName });

    public async Task UpdateLedgerExecutionConfigurationAsync(
        string destinationUrl, string? destinationAuthJson, string connectorKey, string? httpOutcomeJson = null)
    {
        Guid? connectorId = await ScalarAsync<Guid?>(
            $"SELECT id FROM connectors WHERE {database.KeyColumn}=@ConnectorKey AND contract_version=1",
            new { ConnectorKey = connectorKey });
        if (connectorId is null)
        {
            connectorId = Guid.NewGuid();
            await ExecuteAsync($$$"""
                INSERT INTO connectors (id,{{{database.KeyColumn}}},contract_version,manifest_schema_version,name,direction,
                    supported_auth_schemes,status,manifest)
                VALUES (@Id,@ConnectorKey,1,1,@ConnectorKey,'both',{{{database.Json("@Schemes")}}},'active',{{{database.Json("@Manifest")}}})
                """, new
                {
                    Id = connectorId.Value, ConnectorKey = connectorKey, Schemes = "[]",
                    Manifest = TestConnectorManifest.Create(connectorKey, connectorKey, "both", httpOutcomeJson: httpOutcomeJson)
                });
        }
        await ExecuteAsync($$$"""
            UPDATE connections SET config={{{database.Json("@Config")}}},
                destination_authentication={{{database.Json("@DestinationAuth")}}}, connector_id=@ConnectorId
            WHERE id=@LedgerConnectionId
            """, new
            {
                Config = JsonSerializer.Serialize(new { base_uri = destinationUrl }),
                DestinationAuth = destinationAuthJson,
                ConnectorId = connectorId.Value,
                LedgerConnectionId
            });
    }

    public Task ClearLedgerConnectionUrlAsync() => ExecuteAsync(
        $"UPDATE connections SET config={database.Json("@Config")} WHERE id=@LedgerConnectionId",
        new { Config = "{}", LedgerConnectionId });

    public async Task<SubscriptionDeliverySnapshot> GetSubscriptionDeliverySnapshotAsync(Guid eventId)
    {
        SnapshotRow row = (await QueryAsync<SnapshotRow>($$$"""
            SELECT {{{database.JsonText("http_execution_snapshot")}}} AS HttpExecutionSnapshotJson,
                connector_key AS ConnectorKey, {{{database.JsonText("transform_config_snapshot")}}} AS TransformConfigJson
            FROM subscription_deliveries WHERE event_id=@EventId
            """, new { EventId = eventId })).SingleOrDefault()
            ?? throw new InvalidOperationException($"No SubscriptionDelivery exists for Event {eventId}.");
        return new(row.HttpExecutionSnapshotJson, row.ConnectorKey, row.TransformConfigJson);
    }

    public async Task<SubscriptionDto> UpdateLedgerHttpDeliveryAsync(HttpDeliveryConfiguration httpDelivery)
    {
        SubscriptionIdentity identity = (await QueryAsync<SubscriptionIdentity>(
            "SELECT id AS Id, topic_id AS TopicId FROM subscriptions WHERE name='to-ledger'"))
            .SingleOrDefault() ?? throw new InvalidOperationException("The ledger Subscription does not exist.");
        Subscription existing = await subscriptionRepository.GetByIdAsync(
            TenantId, identity.TopicId, identity.Id, CancellationToken.None)
            ?? throw new InvalidOperationException("The ledger Subscription could not be loaded.");
        Subscription? updated = await subscriptionRepository.UpdateAsync(
            TenantId, identity.TopicId, identity.Id, existing.Name, existing.MatchRules,
            existing.DestinationConnectionId, existing.TransformConfig, httpDelivery,
            existing.OrderIndex, existing.Description, CancellationToken.None);
        return SubscriptionDto.From(updated ?? throw new InvalidOperationException("The ledger Subscription could not be updated."));
    }

    public Task<DeadLetterReplayResult> ReplayAsync(
        Guid eventId,
        Guid deliveryId,
        CancellationToken cancellationToken = default) =>
        mediator.Send(new ReplaySubscriptionDeliveryCommand(TenantId, eventId, deliveryId), cancellationToken);
    public Task<EventDto?> GetEventDetailsAsync(Guid eventId, CancellationToken cancellationToken = default) =>
        eventLookup.GetByIdAsync(TenantId, eventId, cancellationToken);

    private Task InsertEventRowAsync(
        Guid eventId, Guid tenantId, Guid sourceConnectionId, string eventType, Guid? topicId)
    {
        string payload = JsonSerializer.Serialize(new { test = true });
        return ExecuteAsync($$$"""
            INSERT INTO events (id,tenant_id,topic_id,source_connection_id,event_type,payload,status,accepted_at)
            VALUES (@Id,@TenantId,@TopicId,@SourceConnectionId,@EventType,{{{database.Json("@Payload")}}},'accepted',{{{database.Now}}});
            INSERT INTO outbox (event_id,payload) VALUES (@Id,{{{database.Json("@Payload")}}});
            """, new { Id = eventId, TenantId = tenantId, TopicId = topicId, SourceConnectionId = sourceConnectionId, EventType = eventType, Payload = payload });
    }

    private Task SeedRoutingDataAsync(DbConnection connection)
    {
        string hash = "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(TenantToken))).ToLowerInvariant();
        return connection.ExecuteAsync($$$"""
            INSERT INTO connectors (id,{{{database.KeyColumn}}},contract_version,manifest_schema_version,name,direction,
                supported_auth_schemes,status,manifest)
            VALUES (@ConnectorId,'http',1,1,'HTTP','both',{{{database.Json("@Schemes")}}},'active',{{{database.Json("@Manifest")}}});
            INSERT INTO tenants (id,slug,name,status,created_at,updated_at) VALUES
                (@TenantId,'test-routing-tenant','Test Routing Tenant','active',{{{database.Now}}},{{{database.Now}}}),
                (@OrphanTenantId,'test-orphan-tenant','Test Orphan Tenant','active',{{{database.Now}}},{{{database.Now}}});
            INSERT INTO api_keys (id,tenant_id,name,key_prefix,key_hash,status,created_at)
            VALUES (@ApiKeyId,@TenantId,'test-key',@KeyPrefix,@KeyHash,'active',{{{database.Now}}});
            INSERT INTO connections (id,tenant_id,connector_id,name,config,status) VALUES
                (@SourceConnectionId,@TenantId,@ConnectorId,'source',{{{database.Json("@EmptyConfig")}}},'active'),
                (@OrphanSourceConnectionId,@OrphanTenantId,@ConnectorId,'orphan-source',{{{database.Json("@EmptyConfig")}}},'active'),
                (@LedgerConnectionId,@TenantId,@ConnectorId,'ledger-sink',{{{database.Json("@LedgerConfig")}}},'active'),
                (@RiskConnectionId,@TenantId,@ConnectorId,'risk-sink',{{{database.Json("@RiskConfig")}}},'active');
            INSERT INTO topics (id,tenant_id,name,status) VALUES (@TopicId,@TenantId,'test-topic','active');
            INSERT INTO topic_sources (tenant_id,topic_id,connection_id) VALUES (@TenantId,@TopicId,@SourceConnectionId);
            INSERT INTO subscriptions (id,tenant_id,topic_id,name,match_rules,destination_connection_id,order_index,status) VALUES
                (@LedgerSubscriptionId,@TenantId,@TopicId,'to-ledger',{{{database.Json("@LedgerRules")}}},@LedgerConnectionId,0,'active'),
                (@RiskSubscriptionId,@TenantId,@TopicId,'to-risk',{{{database.Json("@RiskRules")}}},@RiskConnectionId,1,'active');
            """, new
        {
            ConnectorId = HttpConnectorId, Schemes = "[]", Manifest = TestConnectorManifest.Create("http", "HTTP", "both"),
            TenantId, OrphanTenantId, ApiKeyId = Guid.NewGuid(), KeyPrefix = TenantToken[..12], KeyHash = hash,
            SourceConnectionId, OrphanSourceConnectionId, LedgerConnectionId, RiskConnectionId,
            EmptyConfig = "{}", LedgerConfig = JsonSerializer.Serialize(new { base_uri = LedgerSinkUrl }),
            RiskConfig = JsonSerializer.Serialize(new { base_uri = RiskSinkUrl }), TopicId,
            LedgerSubscriptionId = Guid.NewGuid(), RiskSubscriptionId = Guid.NewGuid(),
            LedgerRules = "{\"event_types\":[\"payment.created\",\"payment.settled\",\"payment.multi\"]}",
            RiskRules = "{\"event_types\":[\"payment.authorized\",\"payment.multi\"]}"
        });
    }

    internal DbConnection CreateConnection() => database.CreateConnection();
    internal async Task<bool> LockOutboxRowAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid eventId)
    {
        string sql = database.Provider == "sqlserver"
            ? "SELECT id FROM outbox WITH (UPDLOCK, ROWLOCK) WHERE event_id = @EventId"
            : "SELECT id FROM outbox WHERE event_id = @EventId FOR UPDATE";
        return await connection.ExecuteScalarAsync<Guid?>(
            sql, new { EventId = eventId }, transaction) is not null;
    }

    internal Task WithOutboxCompletionFailureAsync(Func<Task> action) => WithFaultAsync(
        """
        CREATE FUNCTION fail_outbox_completion() RETURNS trigger AS $$
        BEGIN
            IF OLD.processed_at IS NULL AND NEW.processed_at IS NOT NULL THEN
                RAISE EXCEPTION 'simulated interruption before outbox completion';
            END IF;
            RETURN NEW;
        END;
        $$ LANGUAGE plpgsql;
        CREATE TRIGGER fail_outbox_completion BEFORE UPDATE ON outbox
            FOR EACH ROW EXECUTE FUNCTION fail_outbox_completion();
        """,
        """
        DROP TRIGGER IF EXISTS fail_outbox_completion ON outbox;
        DROP FUNCTION IF EXISTS fail_outbox_completion();
        """,
        """
        CREATE TRIGGER fail_outbox_completion ON outbox AFTER UPDATE AS
        BEGIN
            IF UPDATE(processed_at) THROW 51002, 'simulated interruption before outbox completion', 1;
        END
        """,
        "DROP TRIGGER IF EXISTS fail_outbox_completion",
        action);

    internal Task WithDeliveryClaimFailureAsync(Func<Task> action) => WithFaultAsync(
        """
        CREATE FUNCTION test_fail_delivery_claim() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            RAISE EXCEPTION 'injected claim failure';
        END $$;
        CREATE TRIGGER test_fail_delivery_claim BEFORE UPDATE ON subscription_deliveries
            FOR EACH ROW WHEN (OLD.status = 'pending' AND NEW.status = 'in_flight')
            EXECUTE FUNCTION test_fail_delivery_claim();
        """,
        """
        DROP TRIGGER IF EXISTS test_fail_delivery_claim ON subscription_deliveries;
        DROP FUNCTION IF EXISTS test_fail_delivery_claim();
        """,
        """
        CREATE TRIGGER test_fail_delivery_claim ON subscription_deliveries AFTER UPDATE AS
        BEGIN
            IF EXISTS (SELECT 1 FROM inserted i JOIN deleted d ON i.id=d.id
                WHERE d.status=N'pending' AND i.status=N'in_flight')
                THROW 51004, 'injected claim failure', 1;
        END
        """,
        "DROP TRIGGER IF EXISTS test_fail_delivery_claim",
        action);

    internal Task WithDeliveryFinalizationFailureAsync(Func<Task> action) => WithFaultAsync(
        """
        CREATE FUNCTION test_fail_delivery_finalization() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            RAISE EXCEPTION 'injected finalization failure';
        END $$;
        CREATE TRIGGER test_fail_delivery_finalization BEFORE UPDATE ON subscription_deliveries
            FOR EACH ROW WHEN (OLD.status = 'in_flight' AND NEW.status <> 'in_flight')
            EXECUTE FUNCTION test_fail_delivery_finalization();
        """,
        """
        DROP TRIGGER IF EXISTS test_fail_delivery_finalization ON subscription_deliveries;
        DROP FUNCTION IF EXISTS test_fail_delivery_finalization();
        """,
        """
        CREATE TRIGGER test_fail_delivery_finalization ON subscription_deliveries AFTER UPDATE AS
        BEGIN
            IF EXISTS (SELECT 1 FROM inserted i JOIN deleted d ON i.id=d.id
                WHERE d.status=N'in_flight' AND i.status<>N'in_flight')
                THROW 51005, 'injected finalization failure', 1;
        END
        """,
        "DROP TRIGGER IF EXISTS test_fail_delivery_finalization",
        action);

    internal async Task WithTransientFinalizationFailureAsync(Func<Task> action)
    {
        if (database.Provider == "postgres")
        {
            await WithFaultAsync(
                """
                CREATE SEQUENCE test_finalization_retry_sequence;
                CREATE FUNCTION test_fail_first_attempt_finalization() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF nextval('test_finalization_retry_sequence') = 1 THEN
                        RAISE EXCEPTION 'injected transient finalization failure' USING ERRCODE = '40001';
                    END IF;
                    RETURN NEW;
                END $$;
                CREATE TRIGGER test_fail_first_attempt_finalization BEFORE UPDATE ON delivery_attempts
                    FOR EACH ROW WHEN (OLD.status = 'in_progress' AND NEW.status <> 'in_progress')
                    EXECUTE FUNCTION test_fail_first_attempt_finalization();
                """,
                """
                DROP TRIGGER IF EXISTS test_fail_first_attempt_finalization ON delivery_attempts;
                DROP FUNCTION IF EXISTS test_fail_first_attempt_finalization();
                DROP SEQUENCE IF EXISTS test_finalization_retry_sequence;
                """,
                "",
                "",
                action);
            return;
        }

        await WithSqlServerFinalizationDeadlockAsync(action);
    }

    private async Task WithSqlServerFinalizationDeadlockAsync(Func<Task> action)
    {
        const string resource = "transient-finalization";
        await using DbConnection control = database.CreateConnection();
        await control.OpenAsync();
        await control.ExecuteAsync("CREATE TABLE ##finalization_retry_signal (hit bit NOT NULL)");
        await control.ExecuteAsync($"""
            CREATE TRIGGER test_fail_first_attempt_finalization ON delivery_attempts AFTER UPDATE AS
            BEGIN
                IF EXISTS (SELECT 1 FROM inserted WHERE status=N'succeeded')
                BEGIN
                    SET DEADLOCK_PRIORITY LOW;
                    INSERT INTO ##finalization_retry_signal VALUES (1);
                    DECLARE @result int;
                    EXEC @result=sp_getapplock @Resource=N'{resource}', @LockMode='Exclusive',
                        @LockOwner='Transaction', @LockTimeout=10000;
                END
            END
            """);

        await using var blocker = new SqlConnection(ConnectionString);
        await blocker.OpenAsync();
        await using SqlTransaction transaction = (SqlTransaction)await blocker.BeginTransactionAsync();
        await blocker.ExecuteAsync(
            "DECLARE @r int; EXEC @r=sp_getapplock @Resource=@Resource, @LockMode='Exclusive', @LockOwner='Transaction';",
            new { Resource = resource }, transaction);
        Task actionTask = action();

        try
        {
            await WaitUntilAsync(
                async () => await control.ExecuteScalarAsync<int>(
                    "SELECT COUNT(*) FROM ##finalization_retry_signal WITH (NOLOCK)") > 0,
                "Finalization did not reach the SQL Server deadlock barrier within five seconds.");
            await blocker.ExecuteAsync(
                "UPDATE subscription_deliveries SET updated_at=updated_at WHERE status=N'in_flight'",
                transaction: transaction).WaitAsync(TimeSpan.FromSeconds(5));
            await transaction.RollbackAsync();
            await actionTask;
        }
        finally
        {
            try
            {
                if (transaction.Connection is not null) await transaction.RollbackAsync();
                await actionTask;
            }
            catch { }
            await control.ExecuteAsync(
                "DROP TRIGGER IF EXISTS test_fail_first_attempt_finalization; DROP TABLE IF EXISTS ##finalization_retry_signal;");
        }
    }

    internal async Task RunExpiredLeaseRaceAsync(bool finalizationWins)
    {
        Guid deliveryId = await FanoutSingleDeliveryAsync();
        SubscriptionDeliveryWorkItem first = Assert.IsType<SubscriptionDeliveryWorkItem>(
            await DeliveryQueue.ClaimNextAsync(CancellationToken.None));
        await ForceLeaseExpiredAsync(deliveryId);

        (DeliveryFinalizationResult finalization, SubscriptionDeliveryWorkItem? reclaim) =
            database.Provider == "postgres"
                ? await RunPostgresExpiredLeaseRaceAsync(first, finalizationWins)
                : await RunSqlServerExpiredLeaseRaceAsync(first, finalizationWins);

        if (finalizationWins)
        {
            Assert.Equal(DeliveryFinalizationStatus.Applied, finalization.Status);
            Assert.Equal(SubscriptionDeliveryDisposition.Succeeded, finalization.Disposition);
            Assert.Null(reclaim);
            Assert.Equal("succeeded", (await GetSubscriptionDeliveryAsync(deliveryId)).Status);
            Assert.Equal("succeeded", Assert.Single(await GetDeliveryAttemptsAsync(deliveryId)).Status);
            return;
        }

        Assert.Equal(DeliveryFinalizationStatus.OwnershipLost, finalization.Status);
        Assert.NotNull(reclaim);
        Assert.Equal(2, reclaim.AttemptNumber);
        Assert.Equal(
            ["indeterminate", "in_progress"],
            (await GetDeliveryAttemptsAsync(deliveryId)).Select(attempt => attempt.Status));
    }

    private async Task<(DeliveryFinalizationResult, SubscriptionDeliveryWorkItem?)>
        RunPostgresExpiredLeaseRaceAsync(SubscriptionDeliveryWorkItem first, bool finalizationWins)
    {
        const long advisoryLockKey = 8_931_047_221;
        string blockedStatus = finalizationWins ? "succeeded" : "indeterminate";
        await ExecuteAsync($$"""
            CREATE SEQUENCE test_delivery_race_sequence;
            CREATE FUNCTION test_block_delivery_attempt_update() RETURNS trigger LANGUAGE plpgsql AS $function$
            BEGIN
                PERFORM nextval('test_delivery_race_sequence');
                PERFORM pg_advisory_xact_lock({{advisoryLockKey}});
                RETURN NEW;
            END
            $function$;
            CREATE TRIGGER test_block_delivery_attempt_update BEFORE UPDATE ON delivery_attempts
                FOR EACH ROW WHEN (NEW.status = '{{blockedStatus}}')
                EXECUTE FUNCTION test_block_delivery_attempt_update();
            """);

        await using var barrier = new NpgsqlConnection(ConnectionString);
        await barrier.OpenAsync();
        await barrier.ExecuteScalarAsync("SELECT pg_advisory_lock(@LockKey)", new { LockKey = advisoryLockKey });
        Task<DeliveryFinalizationResult>? finalizationTask = null;
        Task<SubscriptionDeliveryWorkItem?>? reclaimTask = null;

        try
        {
            if (finalizationWins)
                finalizationTask = DeliveryQueue.FinalizeAsync(Success(first), CancellationToken.None);
            else
                reclaimTask = DeliveryQueue.ClaimNextAsync(CancellationToken.None);

            await WaitUntilAsync(async () =>
            {
                await using var connection = new NpgsqlConnection(ConnectionString);
                await connection.OpenAsync();
                return await connection.ExecuteScalarAsync<bool>("SELECT is_called FROM test_delivery_race_sequence");
            }, "The delivery race did not reach its PostgreSQL barrier within five seconds.");

            if (finalizationWins)
            {
                reclaimTask = DeliveryQueue.ClaimNextAsync(CancellationToken.None);
                Assert.Null(await reclaimTask);
            }
            else
            {
                finalizationTask = DeliveryQueue.FinalizeAsync(Success(first), CancellationToken.None);
                await WaitUntilAsync(async () =>
                {
                    await using var connection = new NpgsqlConnection(ConnectionString);
                    await connection.OpenAsync();
                    return await connection.ExecuteScalarAsync<bool>("""
                        SELECT EXISTS (
                            SELECT 1 FROM pg_stat_activity
                            WHERE datname = current_database()
                              AND wait_event_type = 'Lock'
                              AND query LIKE '%FROM subscription_deliveries%FOR UPDATE%')
                        """);
                }, "The stale finalization did not block behind the PostgreSQL reclaim transaction.");
            }

            await barrier.ExecuteScalarAsync("SELECT pg_advisory_unlock(@LockKey)", new { LockKey = advisoryLockKey });
            return (await finalizationTask!, await reclaimTask!);
        }
        finally
        {
            await barrier.ExecuteScalarAsync("SELECT pg_advisory_unlock(@LockKey)", new { LockKey = advisoryLockKey });
            if (finalizationTask is not null) await finalizationTask;
            if (reclaimTask is not null) await reclaimTask;
            await ExecuteAsync("""
                DROP TRIGGER IF EXISTS test_block_delivery_attempt_update ON delivery_attempts;
                DROP FUNCTION IF EXISTS test_block_delivery_attempt_update();
                DROP SEQUENCE IF EXISTS test_delivery_race_sequence;
                """);
        }
    }

    private async Task<(DeliveryFinalizationResult, SubscriptionDeliveryWorkItem?)>
        RunSqlServerExpiredLeaseRaceAsync(SubscriptionDeliveryWorkItem first, bool finalizationWins)
    {
        const string resource = "delivery-race";
        string blockedStatus = finalizationWins ? "succeeded" : "indeterminate";
        await using DbConnection control = database.CreateConnection();
        await control.OpenAsync();
        await control.ExecuteAsync("CREATE TABLE ##delivery_race_signal (hit bit NOT NULL)");
        await control.ExecuteAsync($"""
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

        await using var barrier = new SqlConnection(ConnectionString);
        await barrier.OpenAsync();
        await barrier.ExecuteAsync(
            "DECLARE @r int; EXEC @r=sp_getapplock @Resource=@Resource, @LockMode='Exclusive', @LockOwner='Session';",
            new { Resource = resource });
        bool barrierHeld = true;
        Task<DeliveryFinalizationResult>? finalizationTask = null;
        Task<SubscriptionDeliveryWorkItem?>? reclaimTask = null;

        try
        {
            if (finalizationWins)
                finalizationTask = DeliveryQueue.FinalizeAsync(Success(first), CancellationToken.None);
            else
                reclaimTask = DeliveryQueue.ClaimNextAsync(CancellationToken.None);

            await WaitUntilAsync(
                async () => await control.ExecuteScalarAsync<int>(
                    "SELECT COUNT(*) FROM ##delivery_race_signal WITH (NOLOCK)") > 0,
                "The delivery race did not reach its SQL Server barrier within five seconds.");

            if (finalizationWins)
            {
                reclaimTask = DeliveryQueue.ClaimNextAsync(CancellationToken.None);
                Assert.Null(await reclaimTask);
            }
            else
            {
                finalizationTask = DeliveryQueue.FinalizeAsync(Success(first), CancellationToken.None);
                await Task.Delay(100);
                Assert.False(finalizationTask.IsCompleted);
            }

            await barrier.ExecuteAsync(
                "EXEC sp_releaseapplock @Resource=@Resource, @LockOwner='Session'", new { Resource = resource });
            barrierHeld = false;
            return (await finalizationTask!, await reclaimTask!);
        }
        finally
        {
            if (barrierHeld)
            {
                await barrier.ExecuteAsync(
                    "EXEC sp_releaseapplock @Resource=@Resource, @LockOwner='Session'", new { Resource = resource });
            }
            try
            {
                if (finalizationTask is not null) await finalizationTask;
                if (reclaimTask is not null) await reclaimTask;
            }
            catch { }
            await control.ExecuteAsync(
                "DROP TRIGGER IF EXISTS test_block_delivery_attempt_update; DROP TABLE IF EXISTS ##delivery_race_signal;");
        }
    }

    private static DeliveryAttemptCompletion Success(SubscriptionDeliveryWorkItem item) =>
        new(item.Id, item.AttemptId, true, null, item.PayloadJson, 200, null, null);

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, string timeoutMessage)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(5))
        {
            if (await condition()) return;
            await Task.Delay(10);
        }

        throw new TimeoutException(timeoutMessage);
    }

    private async Task WithFaultAsync(
        string postgresInstall,
        string postgresCleanup,
        string sqlServerInstall,
        string sqlServerCleanup,
        Func<Task> action)
    {
        bool postgres = database.Provider == "postgres";
        await ExecuteAsync(postgres ? postgresInstall : sqlServerInstall);
        try
        {
            await action();
        }
        finally
        {
            await ExecuteAsync(postgres ? postgresCleanup : sqlServerCleanup);
        }
    }

    private async Task<int> ExecuteAsync(string sql, object? parameters = null)
    {
        await using DbConnection connection = database.CreateConnection();
        await connection.OpenAsync();
        return await connection.ExecuteAsync(sql, parameters);
    }
    private async Task<T> ScalarAsync<T>(string sql, object? parameters = null)
    {
        await using DbConnection connection = database.CreateConnection();
        await connection.OpenAsync();
        return (await connection.ExecuteScalarAsync<T>(sql, parameters))!;
    }
    private async Task<IEnumerable<T>> QueryAsync<T>(string sql, object? parameters = null)
    {
        await using DbConnection connection = database.CreateConnection();
        await connection.OpenAsync();
        return await connection.QueryAsync<T>(sql, parameters);
    }
    private static SubscriptionDeliveryState ToState(DeliveryRow row) => new(
        row.Id, row.SubscriptionId, row.Status, row.LifetimeAttemptCount, row.RetryCycleAttemptCount,
        row.ActiveAttemptId, Offset(row.LeaseExpiresAt), Offset(row.DeliverAfter));
    private static DateTimeOffset? Offset(object? value) => value switch
    {
        null or DBNull => null,
        DateTimeOffset dateTimeOffset => dateTimeOffset,
        DateTime dateTime => new DateTimeOffset(DateTime.SpecifyKind(dateTime, DateTimeKind.Utc)),
        _ => throw new InvalidOperationException($"Unsupported database timestamp type {value.GetType().Name}.")
    };

    private sealed record DeliveryRow
    {
        public Guid Id { get; init; }
        public Guid SubscriptionId { get; init; }
        public string Status { get; init; } = string.Empty;
        public int LifetimeAttemptCount { get; init; }
        public int RetryCycleAttemptCount { get; init; }
        public Guid? ActiveAttemptId { get; init; }
        public object? LeaseExpiresAt { get; init; }
        public object? DeliverAfter { get; init; }
    }
    private sealed record AttemptRow
    {
        public Guid Id { get; init; }
        public Guid SubscriptionDeliveryId { get; init; }
        public int AttemptNumber { get; init; }
        public string Status { get; init; } = string.Empty;
        public string? FailurePhase { get; init; }
        public object? CompletedAt { get; init; }
    }
    private sealed record OutboxRetryRow { public int AttemptCount { get; init; } public object? DeliverAfter { get; init; } }
    private sealed record SnapshotRow { public string HttpExecutionSnapshotJson { get; init; } = string.Empty; public string ConnectorKey { get; init; } = string.Empty; public string? TransformConfigJson { get; init; } }
    private sealed record SubscriptionIdentity { public Guid Id { get; init; } public Guid TopicId { get; init; } }
}

public sealed record SubscriptionDeliveryState(
    Guid Id, Guid SubscriptionId, string Status, int LifetimeAttemptCount,
    int RetryCycleAttemptCount, Guid? ActiveAttemptId, DateTimeOffset? LeaseExpiresAt, DateTimeOffset? DeliverAfter)
{
    public int AttemptCount => LifetimeAttemptCount;
}

public sealed record DeliveryAttemptState(
    Guid Id, Guid SubscriptionDeliveryId, int AttemptNumber, string Status,
    string? FailurePhase, DateTimeOffset? CompletedAt);

public sealed record SubscriptionDeliverySnapshot(
    string HttpExecutionSnapshotJson, string ConnectorKey, string? TransformConfigJson);

public sealed class FakeDeliveryClient : IDeliveryClient
{
    public List<DeliveryCall> Calls { get; } = [];
    public bool ShouldSucceed { get; set; } = true;
    public Task<DeliveryResult> DeliverAsync(
        OutboundHttpMessage request, HttpOutcomeContract? outcomeContract, CancellationToken cancellationToken = default)
    {
        Calls.Add(new DeliveryCall(request.Method, request.Uri, request.JsonBody ?? string.Empty, request.Headers));
        return Task.FromResult(ShouldSucceed ? new DeliveryResult(true, 200) : new DeliveryResult(false, 500));
    }
    public void Reset() { Calls.Clear(); ShouldSucceed = true; }
}

public sealed record DeliveryCall(string Method, string Url, string Payload, IReadOnlyDictionary<string, string> Headers);

public sealed class MutableSecretResolver : IDestinationAuthenticationSecretResolver
{
    private readonly Dictionary<string, string> values = new(StringComparer.Ordinal);
    public string ProviderName => "test";
    public void Set(string reference, string value) => values[reference] = value;
    public void Reset() => values.Clear();
    public Task<string> ResolveAsync(TenantSecretScope tenant, string secretName, CancellationToken cancellationToken = default) =>
        values.TryGetValue(secretName, out string? value)
            ? Task.FromResult(value)
            : throw new InvalidOperationException($"Secret reference '{secretName}' is not configured for the test.");
}
