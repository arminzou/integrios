using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Integrios.Application;
using Integrios.Application.Abstractions;
using Integrios.Application.Outbox;
using Integrios.Domain.Contracts;
using Integrios.Infrastructure.Data;
using Integrios.Worker;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Integrios.IntegrationTests;

public sealed class WorkerRoutingTests : IClassFixture<WorkerRoutingFixture>, IAsyncLifetime
{
    private readonly WorkerRoutingFixture fixture;

    public WorkerRoutingTests(WorkerRoutingFixture fixture)
    {
        this.fixture = fixture;
    }

    public async Task InitializeAsync() => await fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Worker_MatchingRoute_DeliversEventAndMarksCompleted()
    {
        var eventId = await fixture.InsertEventAndOutboxAsync("payment.created");

        var processed = await fixture.RunWorkerBatchAsync();

        Assert.Equal(1, processed);
        Assert.Single(fixture.DeliveryClient.Calls);
        Assert.Equal(WorkerRoutingFixture.LedgerSinkUrl, fixture.DeliveryClient.Calls[0].Url);

        var status = await fixture.GetEventStatusAsync(eventId);
        Assert.Equal("completed", status);

        var outboxProcessed = await fixture.IsOutboxRowProcessedAsync(eventId);
        Assert.True(outboxProcessed);
    }

    [Fact]
    public async Task Worker_RouteMatchingSelectsByEventType_CorrectSinkReceivesDelivery()
    {
        var eventId = await fixture.InsertEventAndOutboxAsync("payment.authorized");

        await fixture.RunWorkerBatchAsync();

        Assert.Single(fixture.DeliveryClient.Calls);
        Assert.Equal(WorkerRoutingFixture.RiskSinkUrl, fixture.DeliveryClient.Calls[0].Url);

        var status = await fixture.GetEventStatusAsync(eventId);
        Assert.Equal("completed", status);
    }

    [Fact]
    public async Task Worker_NoMatchingPipeline_SkipsGracefullyAndMarksOutboxProcessed()
    {
        var eventId = await fixture.InsertEventAndOutboxAsync("unknown.event.type");

        var processed = await fixture.RunWorkerBatchAsync();

        Assert.Equal(1, processed);
        Assert.Empty(fixture.DeliveryClient.Calls);

        var outboxProcessed = await fixture.IsOutboxRowProcessedAsync(eventId);
        Assert.True(outboxProcessed);
    }

    [Fact]
    public async Task Worker_DeliveryFailure_SchedulesRetryAndDoesNotMarkProcessed()
    {
        fixture.DeliveryClient.ShouldSucceed = false;
        var eventId = await fixture.InsertEventAndOutboxAsync("payment.created");

        await fixture.RunWorkerBatchAsync();

        Assert.Single(fixture.DeliveryClient.Calls);

        // Event status stays accepted — not failed until dead-lettered
        var status = await fixture.GetEventStatusAsync(eventId);
        Assert.Equal("accepted", status);

        // Outbox row is NOT marked processed — it will be retried
        var outboxProcessed = await fixture.IsOutboxRowProcessedAsync(eventId);
        Assert.False(outboxProcessed);

        var (attemptCount, deliverAfter) = await fixture.GetOutboxRetryStateAsync(eventId);
        Assert.Equal(1, attemptCount);
        Assert.NotNull(deliverAfter);
        Assert.True(deliverAfter > DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task Worker_RetryAfterBackoff_DeliversOnSecondAttempt()
    {
        fixture.DeliveryClient.ShouldSucceed = false;
        var eventId = await fixture.InsertEventAndOutboxAsync("payment.created");

        // First attempt — fails, schedules retry
        await fixture.RunWorkerBatchAsync();
        Assert.Single(fixture.DeliveryClient.Calls);

        // Force deliver_after to the past so the row is immediately eligible
        await fixture.ForceRetryNowAsync(eventId);
        fixture.DeliveryClient.ShouldSucceed = true;

        // Second attempt — should succeed
        var processed = await fixture.RunWorkerBatchAsync();
        Assert.Equal(1, processed);
        Assert.Equal(2, fixture.DeliveryClient.Calls.Count);

        var status = await fixture.GetEventStatusAsync(eventId);
        Assert.Equal("completed", status);

        var outboxProcessed = await fixture.IsOutboxRowProcessedAsync(eventId);
        Assert.True(outboxProcessed);
    }

    [Fact]
    public async Task Worker_RetryBeforeBackoffExpiry_DoesNotRedeliver()
    {
        fixture.DeliveryClient.ShouldSucceed = false;
        var eventId = await fixture.InsertEventAndOutboxAsync("payment.created");

        // First attempt — fails, schedules retry in the future
        await fixture.RunWorkerBatchAsync();
        Assert.Single(fixture.DeliveryClient.Calls);

        // Do NOT advance deliver_after — row is still in the future
        fixture.DeliveryClient.ShouldSucceed = true;

        // Second poll — row is not yet due, so no delivery
        var processed = await fixture.RunWorkerBatchAsync();
        Assert.Equal(0, processed);
        Assert.Single(fixture.DeliveryClient.Calls); // no new calls
    }

    [Fact]
    public async Task Worker_ExhaustsRetries_DeadLettersEventAndStopsRetrying()
    {
        fixture.DeliveryClient.ShouldSucceed = false;
        var eventId = await fixture.InsertEventAndOutboxAsync("payment.created");

        // Fail attempts up to MaxAttempts - 1, each time forcing deliver_after into the past
        for (var i = 1; i < OutboxWorker.MaxAttempts; i++)
        {
            await fixture.RunWorkerBatchAsync();
            await fixture.ForceRetryNowAsync(eventId);
        }

        // Final attempt — should dead-letter
        await fixture.RunWorkerBatchAsync();

        var status = await fixture.GetEventStatusAsync(eventId);
        Assert.Equal("dead_lettered", status);

        var outboxProcessed = await fixture.IsOutboxRowProcessedAsync(eventId);
        Assert.True(outboxProcessed);
    }

    [Fact]
    public async Task Worker_DeadLetteredEvent_IsNotPickedUpAgain()
    {
        fixture.DeliveryClient.ShouldSucceed = false;
        var eventId = await fixture.InsertEventAndOutboxAsync("payment.created");

        for (var i = 1; i < OutboxWorker.MaxAttempts; i++)
        {
            await fixture.RunWorkerBatchAsync();
            await fixture.ForceRetryNowAsync(eventId);
        }
        await fixture.RunWorkerBatchAsync(); // dead-letters

        fixture.DeliveryClient.Reset();
        fixture.DeliveryClient.ShouldSucceed = true;

        var processed = await fixture.RunWorkerBatchAsync();
        Assert.Equal(0, processed);
        Assert.Empty(fixture.DeliveryClient.Calls);
    }

    [Fact]
    public async Task Worker_TenantIsolation_OnlyRoutesWithinTenant()
    {
        // Insert events for both test tenant and a different tenant; only test tenant has routing config.
        var eventId = await fixture.InsertEventAndOutboxAsync("payment.created");
        var orphanEventId = await fixture.InsertOrphanEventAndOutboxAsync("payment.created");

        await fixture.RunWorkerBatchAsync();

        // Only one delivery — the orphan tenant has no pipeline
        Assert.Single(fixture.DeliveryClient.Calls);

        Assert.Equal("completed", await fixture.GetEventStatusAsync(eventId));
        // Orphan outbox row is still marked processed (skipped gracefully)
        Assert.True(await fixture.IsOutboxRowProcessedAsync(orphanEventId));
    }
}

public sealed class WorkerRoutingFixture : IAsyncLifetime
{
    public const string TenantPublicKey = "routing_test_key";
    public const string TenantSecret = "routing_test_secret";
    public const string LedgerSinkUrl = "http://test-sink/ledger";
    public const string RiskSinkUrl = "http://test-sink/risk";

    private static readonly Guid WebhookIntegrationId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid TenantId = Guid.Parse("cccccccc-0000-0000-0000-000000000001");
    private static readonly Guid OrphanTenantId = Guid.Parse("cccccccc-0000-0000-0000-000000000009");
    private static readonly Guid SourceConnectionId = Guid.Parse("cccccccc-0000-0000-0000-000000000002");
    private static readonly Guid LedgerConnectionId = Guid.Parse("cccccccc-0000-0000-0000-000000000003");
    private static readonly Guid RiskConnectionId = Guid.Parse("cccccccc-0000-0000-0000-000000000004");
    private static readonly Guid PipelineId = Guid.Parse("cccccccc-0000-0000-0000-000000000005");

    private readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("integrios")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .Build();

    public FakeDeliveryClient DeliveryClient { get; } = new();
    public string ConnectionString => container.GetConnectionString();

    private IDbConnectionFactory connectionFactory = null!;
    private IOutboxRepository outboxRepository = null!;
    private IRoutingRepository routingRepository = null!;
    private IDeliveryAttemptRepository deliveryAttemptRepository = null!;
    private IEventRepository eventRepository = null!;
    private IMediator mediator = null!;

    public async Task InitializeAsync()
    {
        await container.StartAsync();
        await RunMigrationsAsync();

        var dataSource = new NpgsqlDataSourceBuilder(ConnectionString).Build();
        connectionFactory = new NpgsqlConnectionFactory(dataSource);
        outboxRepository = new OutboxRepository(connectionFactory);
        routingRepository = new RoutingRepository(connectionFactory);
        deliveryAttemptRepository = new DeliveryAttemptRepository(connectionFactory);
        eventRepository = new EventRepository(connectionFactory);

        var services = new ServiceCollection();
        services.AddIntegriosApplication();
        services.AddSingleton<IOutboxRepository>(outboxRepository);
        services.AddSingleton<IRoutingRepository>(routingRepository);
        services.AddSingleton<IDeliveryAttemptRepository>(deliveryAttemptRepository);
        services.AddSingleton<IDeliveryClient>(_ => DeliveryClient);
        services.AddLogging();
        mediator = services.BuildServiceProvider().GetRequiredService<IMediator>();
    }

    public async Task DisposeAsync() => await container.DisposeAsync();

    public async Task ResetAsync()
    {
        DeliveryClient.Reset();

        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();

        await using (var truncateCmd = new NpgsqlCommand(
            "TRUNCATE TABLE delivery_attempts, outbox, events, routes, pipelines, connections, api_keys, tenants, integrations RESTART IDENTITY CASCADE;",
            connection))
        {
            await truncateCmd.ExecuteNonQueryAsync();
        }

        await SeedRoutingDataAsync(connection);
    }

    public Task<int> RunWorkerBatchAsync() =>
        mediator.Send(new ProcessOutboxBatchCommand(10, OutboxWorker.MaxAttempts));

    public async Task<Guid> InsertEventAndOutboxAsync(string eventType)
    {
        var eventId = Guid.NewGuid();
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await InsertEventRowAsync(connection, eventId, TenantId, eventType);
        return eventId;
    }

    public async Task<Guid> InsertOrphanEventAndOutboxAsync(string eventType)
    {
        var eventId = Guid.NewGuid();
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await InsertEventRowAsync(connection, eventId, OrphanTenantId, eventType);
        return eventId;
    }

    public async Task<string?> GetEventStatusAsync(Guid eventId)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT status FROM events WHERE id = @Id", connection);
        cmd.Parameters.AddWithValue("Id", eventId);
        return (string?)await cmd.ExecuteScalarAsync();
    }

    public async Task<bool> IsOutboxRowProcessedAsync(Guid eventId)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT processed_at IS NOT NULL FROM outbox WHERE event_id = @EventId", connection);
        cmd.Parameters.AddWithValue("EventId", eventId);
        return (bool?)await cmd.ExecuteScalarAsync() ?? false;
    }

    public async Task<(int AttemptCount, DateTimeOffset? DeliverAfter)> GetOutboxRetryStateAsync(Guid eventId)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT attempt_count, deliver_after FROM outbox WHERE event_id = @EventId", connection);
        cmd.Parameters.AddWithValue("EventId", eventId);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            throw new InvalidOperationException($"No outbox row for event {eventId}");
        var count = reader.GetInt32(0);
        var after = reader.IsDBNull(1) ? (DateTimeOffset?)null : reader.GetFieldValue<DateTimeOffset>(1);
        return (count, after);
    }

    public async Task ForceRetryNowAsync(Guid eventId)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "UPDATE outbox SET deliver_after = now() - interval '1 second' WHERE event_id = @EventId", connection);
        cmd.Parameters.AddWithValue("EventId", eventId);
        await cmd.ExecuteNonQueryAsync();
    }

    public Task<bool> ReplayAsync(Guid eventId, CancellationToken cancellationToken = default)
        => eventRepository.ReplayEventAsync(TenantId, eventId, cancellationToken);

    private static async Task InsertEventRowAsync(NpgsqlConnection connection, Guid eventId, Guid tenantId, string eventType)
    {
        var payload = JsonSerializer.Serialize(new { test = true });
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO events (id, tenant_id, event_type, payload, status, accepted_at)
            VALUES (@Id, @TenantId, @EventType, @Payload::jsonb, 'accepted', now());
            INSERT INTO outbox (event_id, payload)
            VALUES (@Id, @Payload::jsonb);
            """, connection);
        cmd.Parameters.AddWithValue("Id", eventId);
        cmd.Parameters.AddWithValue("TenantId", tenantId);
        cmd.Parameters.AddWithValue("EventType", eventType);
        cmd.Parameters.AddWithValue("Payload", payload);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task SeedRoutingDataAsync(NpgsqlConnection connection)
    {
        var secretHash = "sha256:" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(TenantSecret))).ToLowerInvariant();

        await using var cmd = new NpgsqlCommand("""
            INSERT INTO integrations (id, key, name, direction, status)
            VALUES (@IntegrationId, 'webhook', 'Webhook', 'both', 'active');

            INSERT INTO tenants (id, slug, name, status, created_at, updated_at)
            VALUES
                (@TenantId,       'test-routing-tenant', 'Test Routing Tenant', 'active', now(), now()),
                (@OrphanTenantId, 'test-orphan-tenant',  'Test Orphan Tenant',  'active', now(), now());

            INSERT INTO api_keys (id, tenant_id, name, key_id, secret_hash, scopes, status, created_at)
            VALUES (@ApiKeyId, @TenantId, 'test-key', @PublicKey, @SecretHash, '{}', 'active', now());

            INSERT INTO connections (id, tenant_id, integration_id, name, config, status)
            VALUES
                (@SourceConnectionId, @TenantId, @IntegrationId, 'source',        '{}',                              'active'),
                (@LedgerConnectionId, @TenantId, @IntegrationId, 'ledger-sink',   @LedgerConfig::jsonb,              'active'),
                (@RiskConnectionId,   @TenantId, @IntegrationId, 'risk-sink',     @RiskConfig::jsonb,                'active');

            INSERT INTO pipelines (id, tenant_id, name, source_connection_id, event_types, status)
            VALUES (@PipelineId, @TenantId, 'test-pipeline', @SourceConnectionId,
                    ARRAY['payment.created', 'payment.settled', 'payment.authorized'], 'active');

            INSERT INTO routes (id, pipeline_id, name, match_rules, destination_connection_id, order_index, status)
            VALUES
                (@LedgerRouteId, @PipelineId, 'to-ledger',
                 '{"event_types":["payment.created","payment.settled"]}'::jsonb,
                 @LedgerConnectionId, 0, 'active'),
                (@RiskRouteId, @PipelineId, 'to-risk',
                 '{"event_types":["payment.authorized"]}'::jsonb,
                 @RiskConnectionId, 1, 'active');
            """, connection);

        cmd.Parameters.AddWithValue("IntegrationId", WebhookIntegrationId);
        cmd.Parameters.AddWithValue("TenantId", TenantId);
        cmd.Parameters.AddWithValue("OrphanTenantId", OrphanTenantId);
        cmd.Parameters.AddWithValue("ApiKeyId", Guid.NewGuid());
        cmd.Parameters.AddWithValue("PublicKey", TenantPublicKey);
        cmd.Parameters.AddWithValue("SecretHash", secretHash);
        cmd.Parameters.AddWithValue("SourceConnectionId", SourceConnectionId);
        cmd.Parameters.AddWithValue("LedgerConnectionId", LedgerConnectionId);
        cmd.Parameters.AddWithValue("RiskConnectionId", RiskConnectionId);
        cmd.Parameters.AddWithValue("LedgerConfig", $"{{\"url\":\"{LedgerSinkUrl}\"}}");
        cmd.Parameters.AddWithValue("RiskConfig", $"{{\"url\":\"{RiskSinkUrl}\"}}");
        cmd.Parameters.AddWithValue("PipelineId", PipelineId);
        cmd.Parameters.AddWithValue("LedgerRouteId", Guid.NewGuid());
        cmd.Parameters.AddWithValue("RiskRouteId", Guid.NewGuid());

        await cmd.ExecuteNonQueryAsync();
    }

    private async Task RunMigrationsAsync()
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Integrios.slnx")))
            {
                foreach (var path in Directory.GetFiles(Path.Combine(directory.FullName, "db", "migrations"), "*.sql")
                             .Where(p => !Path.GetFileName(p).StartsWith("V4__"))
                             .OrderBy(Path.GetFileName, StringComparer.Ordinal))
                {
                    var sql = await File.ReadAllTextAsync(path);
                    await using var cmd = new NpgsqlCommand(sql, connection);
                    await cmd.ExecuteNonQueryAsync();
                }
                return;
            }
            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root.");
    }
}

public sealed class FakeDeliveryClient : IDeliveryClient
{
    public List<(string Url, string Payload)> Calls { get; } = [];
    public bool ShouldSucceed { get; set; } = true;

    public Task<DeliveryResult> DeliverAsync(string url, string payloadJson, CancellationToken cancellationToken = default)
    {
        Calls.Add((url, payloadJson));
        var result = ShouldSucceed
            ? new DeliveryResult(true, 200)
            : new DeliveryResult(false, 500);
        return Task.FromResult(result);
    }

    public void Reset()
    {
        Calls.Clear();
        ShouldSucceed = true;
    }
}
