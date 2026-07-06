using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Integrios.Application;
using Integrios.Application.Abstractions;
using Integrios.Application.Delivery;
using Integrios.Application.Events;
using Integrios.Application.Outbox;
using Integrios.Infrastructure.Data;
using Integrios.Infrastructure.Http;
using Integrios.Infrastructure.Transport;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Integrios.IntegrationTests;

public sealed class WorkerRoutingFixture : IAsyncLifetime
{
    public const string TenantToken = "intg_f0f0e1e1d2d2c3c3aabbccddeeff00112233445566778899aabbccddeeff0011";
    public const string LedgerSinkUrl = "http://test-sink/ledger";
    public const string RiskSinkUrl = "http://test-sink/risk";

    private static readonly Guid WebhookIntegrationId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid TenantId = Guid.Parse("cccccccc-0000-0000-0000-000000000001");
    private static readonly Guid OrphanTenantId = Guid.Parse("cccccccc-0000-0000-0000-000000000009");
    private static readonly Guid SourceConnectionId = Guid.Parse("cccccccc-0000-0000-0000-000000000002");
    private static readonly Guid LedgerConnectionId = Guid.Parse("cccccccc-0000-0000-0000-000000000003");
    private static readonly Guid RiskConnectionId = Guid.Parse("cccccccc-0000-0000-0000-000000000004");
    private static readonly Guid TopicId = Guid.Parse("cccccccc-0000-0000-0000-000000000005");

    private readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("integrios")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .Build();

    public FakeDeliveryClient DeliveryClient { get; } = new();
    public string ConnectionString => container.GetConnectionString();

    private IDbConnectionFactory connectionFactory = null!;
    private IOutboxRepository outboxRepository = null!;
    private ISubscriptionRepository subscriptionRepository = null!;
    private ISubscriptionDeliveryRepository subscriptionDeliveryRepository = null!;
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
        subscriptionRepository = new SubscriptionRepository(connectionFactory);
        subscriptionDeliveryRepository = new SubscriptionDeliveryRepository(connectionFactory);
        deliveryAttemptRepository = new DeliveryAttemptRepository(connectionFactory);
        eventRepository = new EventRepository(connectionFactory);

        var services = new ServiceCollection();
        services.AddIntegriosApplication();
        services.AddSingleton<IOutboxRepository>(outboxRepository);
        services.AddSingleton<IEventBus>(_ => new PostgresEventBus(outboxRepository));
        services.AddSingleton<ISubscriptionRepository>(subscriptionRepository);
        services.AddSingleton<ISubscriptionDeliveryRepository>(subscriptionDeliveryRepository);
        services.AddSingleton<ISubscriptionDeliveryQueue>(_ => new PostgresSubscriptionDeliveryQueue(subscriptionDeliveryRepository));
        services.AddSingleton<IDeliveryAttemptRepository>(deliveryAttemptRepository);
        services.AddSingleton<IDeliveryClient>(_ => DeliveryClient);
        services.AddSingleton<ITransformEvaluator, Integrios.Infrastructure.Transform.JsonataTransformEvaluator>();
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
            "TRUNCATE TABLE subscription_deliveries, delivery_attempts, outbox, events, subscriptions, topics, connections, api_keys, tenants, integrations RESTART IDENTITY CASCADE;",
            connection))
        {
            await truncateCmd.ExecuteNonQueryAsync();
        }

        await SeedRoutingDataAsync(connection);
    }

    public async Task<int> RunWorkerBatchAsync()
    {
        await mediator.Send(new ProcessOutboxBatchCommand(10));
        return await mediator.Send(new DispatchSubscriptionDeliveriesCommand(25, RetryPolicy.DefaultMaxAttempts));
    }

    public async Task<IReadOnlyList<SubscriptionDeliveryState>> GetSubscriptionDeliveriesAsync(Guid eventId)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            """
            SELECT subscription_id, status, attempt_count, deliver_after
            FROM subscription_deliveries
            WHERE event_id = @EventId
            ORDER BY created_at
            """, connection);
        cmd.Parameters.AddWithValue("EventId", eventId);
        var rows = new List<SubscriptionDeliveryState>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new SubscriptionDeliveryState(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetInt32(2),
                reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3)));
        }
        return rows;
    }

    public async Task ForceDeliveryRetryNowAsync(Guid eventId)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "UPDATE subscription_deliveries SET deliver_after = now() - interval '1 second' WHERE event_id = @EventId AND status = 'pending'",
            connection);
        cmd.Parameters.AddWithValue("EventId", eventId);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<Guid> InsertEventAndOutboxAsync(string eventType)
    {
        var eventId = Guid.NewGuid();
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await InsertEventRowAsync(connection, eventId, TenantId, eventType, TopicId);
        return eventId;
    }

    public async Task<Guid> InsertOrphanEventAndOutboxAsync(string eventType)
    {
        var eventId = Guid.NewGuid();
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await InsertEventRowAsync(connection, eventId, OrphanTenantId, eventType, topicId: null);
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

    public async Task SetSubscriptionTransformByNameAsync(string subscriptionName, string? transformConfigJson)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "UPDATE subscriptions SET transform_config = @Config::jsonb WHERE name = @Name",
            connection);
        cmd.Parameters.AddWithValue("Config", transformConfigJson ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("Name", subscriptionName);
        await cmd.ExecuteNonQueryAsync();
    }

    public Task<bool> ReplayAsync(Guid eventId, CancellationToken cancellationToken = default)
        => eventRepository.ReplayEventAsync(TenantId, eventId, cancellationToken);

    public Task<GetEventResponse?> GetEventDetailsAsync(Guid eventId, CancellationToken cancellationToken = default)
        => eventRepository.GetEventByIdAsync(TenantId, eventId, cancellationToken);

    private static async Task InsertEventRowAsync(NpgsqlConnection connection, Guid eventId, Guid tenantId, string eventType, Guid? topicId = null)
    {
        var payload = JsonSerializer.Serialize(new { test = true });
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO events (id, tenant_id, topic_id, event_type, payload, status, accepted_at)
            VALUES (@Id, @TenantId, @TopicId, @EventType, @Payload::jsonb, 'accepted', now());
            INSERT INTO outbox (event_id, payload)
            VALUES (@Id, @Payload::jsonb);
            """, connection);
        cmd.Parameters.AddWithValue("Id", eventId);
        cmd.Parameters.AddWithValue("TenantId", tenantId);
        cmd.Parameters.AddWithValue("TopicId", topicId.HasValue ? (object)topicId.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("EventType", eventType);
        cmd.Parameters.AddWithValue("Payload", payload);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task SeedRoutingDataAsync(NpgsqlConnection connection)
    {
        var secretHash = "sha256:" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(TenantToken))).ToLowerInvariant();

        await using var cmd = new NpgsqlCommand("""
            INSERT INTO integrations (id, key, name, direction, status)
            VALUES (@IntegrationId, 'webhook', 'Webhook', 'both', 'active');

            INSERT INTO tenants (id, slug, name, status, created_at, updated_at)
            VALUES
                (@TenantId,       'test-routing-tenant', 'Test Routing Tenant', 'active', now(), now()),
                (@OrphanTenantId, 'test-orphan-tenant',  'Test Orphan Tenant',  'active', now(), now());

            INSERT INTO api_keys (id, tenant_id, name, key_prefix, key_hash, scopes, status, created_at)
            VALUES (@ApiKeyId, @TenantId, 'test-key', @KeyPrefix, @KeyHash, '{}', 'active', now());

            INSERT INTO connections (id, tenant_id, integration_id, name, config, status)
            VALUES
                (@SourceConnectionId, @TenantId, @IntegrationId, 'source',        '{}',                              'active'),
                (@LedgerConnectionId, @TenantId, @IntegrationId, 'ledger-sink',   @LedgerConfig::jsonb,              'active'),
                (@RiskConnectionId,   @TenantId, @IntegrationId, 'risk-sink',     @RiskConfig::jsonb,                'active');

            INSERT INTO topics (id, tenant_id, name, status)
            VALUES (@TopicId, @TenantId, 'test-topic', 'active');

            INSERT INTO topic_sources (topic_id, connection_id)
            VALUES (@TopicId, @SourceConnectionId);

            -- Intentionally uses the pre-v2.1 event_types[] array shape to cover the
            -- compat read path in SubscriptionRepository.
            INSERT INTO subscriptions (id, topic_id, name, match_rules, destination_connection_id, order_index, status)
            VALUES
                (@LedgerSubscriptionId, @TopicId, 'to-ledger',
                 '{"event_types":["payment.created","payment.settled","payment.multi"]}'::jsonb,
                 @LedgerConnectionId, 0, 'active'),
                (@RiskSubscriptionId, @TopicId, 'to-risk',
                 '{"event_types":["payment.authorized","payment.multi"]}'::jsonb,
                 @RiskConnectionId, 1, 'active');
            """, connection);

        cmd.Parameters.AddWithValue("IntegrationId", WebhookIntegrationId);
        cmd.Parameters.AddWithValue("TenantId", TenantId);
        cmd.Parameters.AddWithValue("OrphanTenantId", OrphanTenantId);
        cmd.Parameters.AddWithValue("ApiKeyId", Guid.NewGuid());
        cmd.Parameters.AddWithValue("KeyPrefix", TenantToken[..12]);
        cmd.Parameters.AddWithValue("KeyHash", secretHash);
        cmd.Parameters.AddWithValue("SourceConnectionId", SourceConnectionId);
        cmd.Parameters.AddWithValue("LedgerConnectionId", LedgerConnectionId);
        cmd.Parameters.AddWithValue("RiskConnectionId", RiskConnectionId);
        cmd.Parameters.AddWithValue("LedgerConfig", $"{{\"url\":\"{LedgerSinkUrl}\"}}");
        cmd.Parameters.AddWithValue("RiskConfig", $"{{\"url\":\"{RiskSinkUrl}\"}}");
        cmd.Parameters.AddWithValue("TopicId", TopicId);
        cmd.Parameters.AddWithValue("LedgerSubscriptionId", Guid.NewGuid());
        cmd.Parameters.AddWithValue("RiskSubscriptionId", Guid.NewGuid());

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
                             .OrderBy(GetMigrationVersion)
                             .ThenBy(Path.GetFileName, StringComparer.Ordinal))
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

    private static int GetMigrationVersion(string path)
    {
        var fileName = Path.GetFileName(path);
        var separator = fileName.IndexOf("__", StringComparison.Ordinal);
        if (separator <= 1)
            return int.MaxValue;

        return int.TryParse(fileName[1..separator], out var version)
            ? version
            : int.MaxValue;
    }
}

public sealed record SubscriptionDeliveryState(Guid SubscriptionId, string Status, int AttemptCount, DateTimeOffset? DeliverAfter);

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
