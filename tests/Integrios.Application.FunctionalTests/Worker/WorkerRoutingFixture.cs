using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Integrios.Application;
using Integrios.Application.Auth;
using Integrios.Application.Delivery;
using Integrios.Application.Events;
using Integrios.Application.Outbox;
using Integrios.Application.Secrets;
using Integrios.Application.Subscriptions;
using Integrios.Application.Transforms;
using Integrios.Domain.Subscriptions;
using Integrios.Infrastructure.Auth;
using Integrios.Infrastructure.Data;
using Integrios.Infrastructure.Delivery;
using Integrios.Infrastructure.Events;
using Integrios.Infrastructure.Outbox;
using Integrios.Infrastructure.Subscriptions;
using Integrios.Infrastructure.Transforms;
using Integrios.Tests.Shared;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Integrios.Application.FunctionalTests.Worker;

public sealed class WorkerRoutingFixture : IAsyncLifetime
{
    public const string TenantToken = "intg_f0f0e1e1d2d2c3c3aabbccddeeff00112233445566778899aabbccddeeff0011";
    public const string LedgerSinkUrl = "http://test-sink/ledger";
    public const string RiskSinkUrl = "http://test-sink/risk";

    private static readonly Guid HttpIntegrationId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid TenantId = Guid.Parse("cccccccc-0000-0000-0000-000000000001");
    private static readonly Guid OrphanTenantId = Guid.Parse("cccccccc-0000-0000-0000-000000000009");
    private static readonly Guid SourceConnectionId = Guid.Parse("cccccccc-0000-0000-0000-000000000002");
    private static readonly Guid OrphanSourceConnectionId = Guid.Parse("cccccccc-0000-0000-0000-000000000008");
    private static readonly Guid LedgerConnectionId = Guid.Parse("cccccccc-0000-0000-0000-000000000003");
    private static readonly Guid RiskConnectionId = Guid.Parse("cccccccc-0000-0000-0000-000000000004");
    private static readonly Guid TopicId = Guid.Parse("cccccccc-0000-0000-0000-000000000005");

    private readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgres:16.14-alpine3.24")
        .WithDatabase("integrios")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .Build();

    public FakeDeliveryClient DeliveryClient { get; } = new();
    public MutableSecretResolver SecretResolver { get; } = new();
    public string ConnectionString => container.GetConnectionString();
    internal PostgresSubscriptionDeliveryQueue DeliveryQueue { get; private set; } = null!;

    private IDbConnectionFactory connectionFactory = null!;
    private IDeadLetterReplay deadLetterReplay = null!;
    private IOutboxFanout outboxFanout = null!;
    private ISubscriptionRepository subscriptionRepository = null!;
    private ITenantEventLookup eventLookup = null!;
    private IMediator mediator = null!;

    public async Task InitializeAsync()
    {
        await container.StartAsync();
        await PostgresMigrationTestHelper.MigrateAsync(ConnectionString);

        var dataSource = new NpgsqlDataSourceBuilder(ConnectionString).Build();
        connectionFactory = new NpgsqlConnectionFactory(dataSource);
        deadLetterReplay = new PostgresDeadLetterReplay(connectionFactory);
        outboxFanout = new PostgresOutboxFanout(connectionFactory);
        subscriptionRepository = new PostgresSubscriptionRepository(connectionFactory);
        eventLookup = new PostgresTenantEventLookup(connectionFactory);
        var deliveryOptions = DeliveryExecutionOptions.Default;
        DeliveryQueue = new PostgresSubscriptionDeliveryQueue(
            connectionFactory,
            deliveryOptions,
            new DeliveryOutcomePolicy(new RetryPolicy()));

        var services = new ServiceCollection();
        services.AddApplicationServices();
        services.AddSingleton(outboxFanout);
        services.AddSingleton(deadLetterReplay);
        services.AddSingleton<ISubscriptionRepository>(subscriptionRepository);
        services.AddSingleton(deliveryOptions);
        services.AddSingleton<ISubscriptionDeliveryQueue>(DeliveryQueue);
        services.AddSingleton<IDeliveryClient>(_ => DeliveryClient);
        services.AddSingleton<IAuthSchemeHandler, ApiKeyHeaderAuthSchemeHandler>();
        services.AddSingleton<IAuthSchemeHandler, BearerTokenAuthSchemeHandler>();
        services.AddSingleton<IAuthSchemeRegistry, AuthSchemeRegistry>();
        services.AddSingleton<IDestinationAuthenticationSecretResolver>(_ => SecretResolver);
        services.AddSingleton<ITransformEvaluator, JsonataTransformEvaluator>();
        services.AddLogging();
        mediator = services.BuildServiceProvider().GetRequiredService<IMediator>();
    }

    public async Task DisposeAsync() => await container.DisposeAsync();

    public async Task ResetAsync()
    {
        DeliveryClient.Reset();
        SecretResolver.Reset();

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
        return await mediator.Send(new DispatchSubscriptionDeliveriesCommand(25));
    }

    public Task<int> RunFanoutBatchAsync(int batchSize = 10)
        => mediator.Send(new ProcessOutboxBatchCommand(batchSize));

    public Task<int> RunDeliveryBatchAsync(int batchSize = 25)
        => mediator.Send(new DispatchSubscriptionDeliveriesCommand(batchSize));

    public async Task<IReadOnlyList<SubscriptionDeliveryState>> GetSubscriptionDeliveriesAsync(Guid eventId)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            """
            SELECT id,
                   subscription_id,
                   status,
                   lifetime_attempt_count,
                   retry_cycle_attempt_count,
                   active_attempt_id,
                   lease_expires_at,
                   deliver_after
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
                reader.GetGuid(1),
                reader.GetString(2),
                reader.GetInt32(3),
                reader.GetInt32(4),
                reader.IsDBNull(5) ? null : reader.GetGuid(5),
                reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6),
                reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7)));
        }
        return rows;
    }

    public async Task<Guid> FanoutSingleDeliveryAsync(string eventType = "payment.created")
    {
        Guid eventId = await InsertEventAndOutboxAsync(eventType);
        int processed = await RunFanoutBatchAsync();
        if (processed != 1)
            throw new InvalidOperationException($"Expected one Event to fan out, but processed {processed}.");

        SubscriptionDeliveryState delivery = Assert.Single(await GetSubscriptionDeliveriesAsync(eventId));
        return delivery.Id;
    }

    public async Task ForceLeaseExpiredAsync(Guid deliveryId)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "UPDATE subscription_deliveries SET lease_expires_at = now() - interval '1 second' WHERE id = @DeliveryId AND status = 'in_flight'",
            connection);
        command.Parameters.AddWithValue("DeliveryId", deliveryId);
        int updated = await command.ExecuteNonQueryAsync();
        if (updated != 1)
            throw new InvalidOperationException($"Delivery {deliveryId} did not have an active lease to expire.");
    }

    public async Task<IReadOnlyList<DeliveryAttemptState>> GetDeliveryAttemptsAsync(Guid deliveryId)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT id, subscription_delivery_id, attempt_number, status, failure_phase, completed_at
            FROM delivery_attempts
            WHERE subscription_delivery_id = @DeliveryId
            ORDER BY attempt_number
            """,
            connection);
        command.Parameters.AddWithValue("DeliveryId", deliveryId);

        var attempts = new List<DeliveryAttemptState>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            attempts.Add(new DeliveryAttemptState(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetInt32(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5)));
        }

        return attempts;
    }

    public async Task<SubscriptionDeliveryState> GetSubscriptionDeliveryAsync(Guid deliveryId)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT id,
                   subscription_id,
                   status,
                   lifetime_attempt_count,
                   retry_cycle_attempt_count,
                   active_attempt_id,
                   lease_expires_at,
                   deliver_after
            FROM subscription_deliveries
            WHERE id = @DeliveryId
            """,
            connection);
        command.Parameters.AddWithValue("DeliveryId", deliveryId);

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            throw new InvalidOperationException($"No SubscriptionDelivery exists with id {deliveryId}.");

        return new SubscriptionDeliveryState(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetString(2),
            reader.GetInt32(3),
            reader.GetInt32(4),
            reader.IsDBNull(5) ? null : reader.GetGuid(5),
            reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6),
            reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7));
    }

    public async Task<int> GetSubscriptionDeliveryCountAsync(Guid eventId)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT COUNT(*) FROM subscription_deliveries WHERE event_id = @EventId",
            connection);
        command.Parameters.AddWithValue("EventId", eventId);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
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
        await InsertEventRowAsync(connection, eventId, TenantId, SourceConnectionId, eventType, TopicId);
        return eventId;
    }

    public async Task<Guid> InsertOrphanEventAndOutboxAsync(string eventType)
    {
        var eventId = Guid.NewGuid();
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await InsertEventRowAsync(
            connection, eventId, OrphanTenantId, OrphanSourceConnectionId, eventType, topicId: null);
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

    public async Task UpdateLedgerExecutionConfigurationAsync(
        string destinationUrl,
        string? destinationAuthJson,
        string integrationKey,
        string? httpOutcomeJson = null)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        // An Integration key is immutable functional identity, so moving the Connection to a
        // different Integration is how its effective key changes. Renaming the row in place is
        // rejected by the database.
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO integrations (
                id, key, contract_version, manifest_schema_version, name, direction, status, manifest)
            VALUES (
                @NewIntegrationId, @IntegrationKey, 1, 1, @IntegrationKey, 'both', 'active', @Manifest::jsonb)
            ON CONFLICT (key, contract_version) DO NOTHING;

            UPDATE connections
            SET config = jsonb_build_object('base_uri', @DestinationUrl),
                destination_authentication = @DestinationAuthJson::jsonb,
                integration_id = (
                    SELECT id FROM integrations WHERE key = @IntegrationKey AND contract_version = 1)
            WHERE id = @LedgerConnectionId;
            """,
            connection);
        cmd.Parameters.AddWithValue("DestinationUrl", destinationUrl);
        cmd.Parameters.AddWithValue("DestinationAuthJson", destinationAuthJson ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("LedgerConnectionId", LedgerConnectionId);
        cmd.Parameters.AddWithValue("IntegrationKey", integrationKey);
        cmd.Parameters.AddWithValue("NewIntegrationId", Guid.NewGuid());
        cmd.Parameters.AddWithValue(
            "Manifest",
            TestIntegrationManifest.Create(integrationKey, integrationKey, "both", httpOutcomeJson: httpOutcomeJson));
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task ClearLedgerConnectionUrlAsync()
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "UPDATE connections SET config = '{}'::jsonb WHERE id = @LedgerConnectionId",
            connection);
        cmd.Parameters.AddWithValue("LedgerConnectionId", LedgerConnectionId);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<SubscriptionDeliverySnapshot> GetSubscriptionDeliverySnapshotAsync(Guid eventId)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            """
            SELECT http_execution_snapshot::text, integration_key, transform_config_snapshot::text
            FROM subscription_deliveries
            WHERE event_id = @EventId
            """,
            connection);
        cmd.Parameters.AddWithValue("EventId", eventId);

        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            throw new InvalidOperationException($"No SubscriptionDelivery exists for Event {eventId}.");

        return new SubscriptionDeliverySnapshot(
            reader.GetString(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2));
    }

    public async Task<SubscriptionDto> UpdateLedgerHttpDeliveryAsync(HttpDeliveryConfiguration httpDelivery)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT id, topic_id FROM subscriptions WHERE name = 'to-ledger'",
            connection);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            throw new InvalidOperationException("The ledger Subscription does not exist.");
        var subscriptionId = reader.GetGuid(0);
        var subscriptionTopicId = reader.GetGuid(1);
        await reader.DisposeAsync();

        Subscription existing = await subscriptionRepository.GetByIdAsync(TenantId, subscriptionTopicId, subscriptionId, CancellationToken.None)
            ?? throw new InvalidOperationException("The ledger Subscription could not be loaded.");
        Subscription? updated = await subscriptionRepository.UpdateAsync(
            TenantId,
            subscriptionTopicId,
            subscriptionId,
            existing.Name,
            existing.MatchRules,
            existing.DestinationConnectionId,
            existing.TransformConfig,
            httpDelivery,
            existing.OrderIndex,
            existing.Description,
            CancellationToken.None);
        return SubscriptionDto.From(updated ?? throw new InvalidOperationException("The ledger Subscription could not be updated."));
    }

    public Task<bool> ReplayAsync(Guid eventId, CancellationToken cancellationToken = default)
        => mediator.Send(new ReplayEventCommand(TenantId, eventId), cancellationToken);

    public Task<EventDto?> GetEventDetailsAsync(Guid eventId, CancellationToken cancellationToken = default)
        => eventLookup.GetByIdAsync(TenantId, eventId, cancellationToken);

    private static async Task InsertEventRowAsync(
        NpgsqlConnection connection,
        Guid eventId,
        Guid tenantId,
        Guid sourceConnectionId,
        string eventType,
        Guid? topicId = null)
    {
        var payload = JsonSerializer.Serialize(new { test = true });
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO events (
                id, tenant_id, topic_id, source_connection_id, event_type, payload, status, accepted_at
            ) VALUES (
                @Id, @TenantId, @TopicId, @SourceConnectionId, @EventType,
                @Payload::jsonb, 'accepted', now());
            INSERT INTO outbox (event_id, payload)
            VALUES (@Id, @Payload::jsonb);
            """, connection);
        cmd.Parameters.AddWithValue("Id", eventId);
        cmd.Parameters.AddWithValue("TenantId", tenantId);
        cmd.Parameters.AddWithValue("TopicId", topicId.HasValue ? (object)topicId.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("SourceConnectionId", sourceConnectionId);
        cmd.Parameters.AddWithValue("EventType", eventType);
        cmd.Parameters.AddWithValue("Payload", payload);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task SeedRoutingDataAsync(NpgsqlConnection connection)
    {
        var secretHash = "sha256:" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(TenantToken))).ToLowerInvariant();

        await using var cmd = new NpgsqlCommand("""
            INSERT INTO integrations (
                id, key, contract_version, manifest_schema_version, name, direction, status, manifest)
            VALUES (
                @IntegrationId, 'http', 1, 1, 'HTTP', 'both', 'active', @Manifest::jsonb);

            INSERT INTO tenants (id, slug, name, status, created_at, updated_at)
            VALUES
                (@TenantId,       'test-routing-tenant', 'Test Routing Tenant', 'active', now(), now()),
                (@OrphanTenantId, 'test-orphan-tenant',  'Test Orphan Tenant',  'active', now(), now());

            INSERT INTO api_keys (id, tenant_id, name, key_prefix, key_hash, status, created_at)
            VALUES (@ApiKeyId, @TenantId, 'test-key', @KeyPrefix, @KeyHash, 'active', now());

            INSERT INTO connections (id, tenant_id, integration_id, name, config, status)
            VALUES
                (@SourceConnectionId, @TenantId, @IntegrationId, 'source',        '{}',                              'active'),
                (@OrphanSourceConnectionId, @OrphanTenantId, @IntegrationId, 'orphan-source', '{}',                 'active'),
                (@LedgerConnectionId, @TenantId, @IntegrationId, 'ledger-sink',   @LedgerConfig::jsonb,              'active'),
                (@RiskConnectionId,   @TenantId, @IntegrationId, 'risk-sink',     @RiskConfig::jsonb,                'active');

            INSERT INTO topics (id, tenant_id, name, status)
            VALUES (@TopicId, @TenantId, 'test-topic', 'active');

            INSERT INTO topic_sources (tenant_id, topic_id, connection_id)
            VALUES (@TenantId, @TopicId, @SourceConnectionId);

            -- Intentionally uses the pre-v2.1 event_types[] array shape to cover the
            -- compat read path in PostgresSubscriptionRepository.
            INSERT INTO subscriptions (id, tenant_id, topic_id, name, match_rules, destination_connection_id, order_index, status)
            VALUES
                (@LedgerSubscriptionId, @TenantId, @TopicId, 'to-ledger',
                 '{"event_types":["payment.created","payment.settled","payment.multi"]}'::jsonb,
                 @LedgerConnectionId, 0, 'active'),
                (@RiskSubscriptionId, @TenantId, @TopicId, 'to-risk',
                 '{"event_types":["payment.authorized","payment.multi"]}'::jsonb,
                 @RiskConnectionId, 1, 'active');
            """, connection);

        cmd.Parameters.AddWithValue("IntegrationId", HttpIntegrationId);
        cmd.Parameters.AddWithValue("Manifest", TestIntegrationManifest.Create("http", "HTTP", "both"));
        cmd.Parameters.AddWithValue("TenantId", TenantId);
        cmd.Parameters.AddWithValue("OrphanTenantId", OrphanTenantId);
        cmd.Parameters.AddWithValue("ApiKeyId", Guid.NewGuid());
        cmd.Parameters.AddWithValue("KeyPrefix", TenantToken[..12]);
        cmd.Parameters.AddWithValue("KeyHash", secretHash);
        cmd.Parameters.AddWithValue("SourceConnectionId", SourceConnectionId);
        cmd.Parameters.AddWithValue("OrphanSourceConnectionId", OrphanSourceConnectionId);
        cmd.Parameters.AddWithValue("LedgerConnectionId", LedgerConnectionId);
        cmd.Parameters.AddWithValue("RiskConnectionId", RiskConnectionId);
        cmd.Parameters.AddWithValue("LedgerConfig", $"{{\"base_uri\":\"{LedgerSinkUrl}\"}}");
        cmd.Parameters.AddWithValue("RiskConfig", $"{{\"base_uri\":\"{RiskSinkUrl}\"}}");
        cmd.Parameters.AddWithValue("TopicId", TopicId);
        cmd.Parameters.AddWithValue("LedgerSubscriptionId", Guid.NewGuid());
        cmd.Parameters.AddWithValue("RiskSubscriptionId", Guid.NewGuid());

        await cmd.ExecuteNonQueryAsync();
    }

}

public sealed record SubscriptionDeliveryState(
    Guid Id,
    Guid SubscriptionId,
    string Status,
    int LifetimeAttemptCount,
    int RetryCycleAttemptCount,
    Guid? ActiveAttemptId,
    DateTimeOffset? LeaseExpiresAt,
    DateTimeOffset? DeliverAfter)
{
    public int AttemptCount => LifetimeAttemptCount;
}

public sealed record DeliveryAttemptState(
    Guid Id,
    Guid SubscriptionDeliveryId,
    int AttemptNumber,
    string Status,
    string? FailurePhase,
    DateTimeOffset? CompletedAt);

public sealed record SubscriptionDeliverySnapshot(
    string HttpExecutionSnapshotJson,
    string IntegrationKey,
    string? TransformConfigJson);

public sealed class FakeDeliveryClient : IDeliveryClient
{
    public List<DeliveryCall> Calls { get; } = [];
    public bool ShouldSucceed { get; set; } = true;

    public Task<DeliveryResult> DeliverAsync(
        OutboundHttpMessage request, HttpOutcomeContract? outcomeContract, CancellationToken cancellationToken = default)
    {
        _ = outcomeContract;
        _ = cancellationToken;
        Calls.Add(new DeliveryCall(request.Method, request.Uri, request.JsonBody ?? string.Empty, request.Headers));
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

public sealed record DeliveryCall(string Method, string Url, string Payload, IReadOnlyDictionary<string, string> Headers);

public sealed class MutableSecretResolver : IDestinationAuthenticationSecretResolver
{
    private readonly Dictionary<string, string> values = new(StringComparer.Ordinal);
    public string ProviderName => "test";

    public void Set(string reference, string value) => values[reference] = value;

    public void Reset() => values.Clear();

    public Task<string> ResolveAsync(TenantSecretScope tenant, string secretName, CancellationToken cancellationToken = default)
    {
        _ = tenant;
        _ = cancellationToken;
        return values.TryGetValue(secretName, out var value)
            ? Task.FromResult(value)
            : throw new InvalidOperationException($"Secret reference '{secretName}' is not configured for the test.");
    }
}
