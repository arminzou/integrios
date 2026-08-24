extern alias IngestionHost;

using System.Data.Common;
using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Dapper;
using Integrios.Tests.Shared;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Testcontainers.ServiceBus;

namespace Integrios.Application.FunctionalTests.Ingestion;

// Real-broker qualification for voe.7: proves PeekLock completion, deterministic dead-lettering,
// and idempotent redelivery against the official Azure Service Bus emulator (Testcontainers.
// ServiceBus), not just the unit-level settlement-decision logic AcceptQueueMessageCommandTests
// covers. Opt-in and excluded from the default `dotnet test` run via the Qualification category,
// same as the Docker/Testcontainers-heavy tests in Integrios.AcceptanceTests.
[Trait("Category", "Qualification")]
public sealed class AzureServiceBusQueueSourceQualificationTests : IAsyncLifetime
{
    private const string TenantSlug = "sb-qualification";
    private const string SecretReference = "sb_connection_string";
    private const string QueueName = "queue.1";

    private readonly FunctionalDatabase database = new();
    private readonly ServiceBusContainer serviceBus =
        new ServiceBusBuilder("mcr.microsoft.com/azure-messaging/servicebus-emulator:latest")
            .WithAcceptLicenseAgreement(true)
            .Build();

    private WebApplicationFactory<IngestionHost::Program> factory = null!;
    private Guid tenantId;

    public async Task InitializeAsync()
    {
        await database.StartAsync();
        await serviceBus.StartAsync();

        tenantId = await SeedAsync();

        factory = new WebApplicationFactory<IngestionHost::Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Integrios:SourceSecrets:Provider", "configuration");
            builder.UseSetting("Database:Provider", database.Provider);
            builder.UseSetting($"ConnectionStrings:{database.ConnectionName}", database.ConnectionString);
            builder.ConfigureAppConfiguration((_, config) => config.AddConfiguration(database.Configuration));
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    [$"SourceSecrets:{TenantSlug}:{SecretReference}"] = serviceBus.GetConnectionString(),
                }));
        });

        // WebApplicationFactory builds the host lazily; force it now so
        // AzureServiceBusQueueReceiver.StartAsync has already loaded and connected its processor
        // before any test publishes a message.
        using HttpClient warmup = factory.CreateClient();
        await warmup.GetAsync("/health");
    }

    public async Task DisposeAsync()
    {
        factory.Dispose();
        await serviceBus.DisposeAsync();
        await database.DisposeAsync();
    }

    [Fact]
    public async Task ValidMessage_CompletesAndCreatesEvent()
    {
        string sourceEventId = $"evt-{Guid.NewGuid():N}";
        await PublishAsync(new
        {
            event_type = "order.created",
            source_event_id = sourceEventId,
            payload = new { amount = 4200 },
        });

        Guid eventId = await WaitForEventAsync(sourceEventId);

        EventRow row = await QuerySingleAsync<EventRow>(
            "SELECT tenant_id AS TenantId, event_type AS EventType, status AS Status FROM events WHERE id=@Id",
            new { Id = eventId });
        Assert.Equal(tenantId, row.TenantId);
        Assert.Equal("order.created", row.EventType);

        // Complete removes it from the active queue; nothing should be left to redeliver.
        await AssertQueueIsEmptyAsync();
    }

    [Fact]
    public async Task SchemaInvalidMessage_DeadLettersWithoutCreatingEvent()
    {
        string sourceEventId = $"evt-{Guid.NewGuid():N}";
        // event_type must be a string per the mapping's own downstream output validation; a missing
        // event_type is a deterministic Source rejection.
        await PublishAsync(new { source_event_id = sourceEventId, payload = new { } });

        ServiceBusReceivedMessage? deadLettered = await ReceiveDeadLetterAsync();
        Assert.NotNull(deadLettered);
        Assert.Equal("source_rejection", deadLettered!.DeadLetterReason);

        long eventCount = await QuerySingleAsync<long>(
            "SELECT COUNT(*) FROM events WHERE tenant_id=@TenantId AND source_event_id=@SourceEventId",
            new { TenantId = tenantId, SourceEventId = sourceEventId });
        Assert.Equal(0, eventCount);
    }

    [Fact]
    public async Task DuplicateSourceEventId_CompletesBothWithoutDuplicateEvent()
    {
        string sourceEventId = $"evt-{Guid.NewGuid():N}";
        object message = new
        {
            event_type = "order.created",
            source_event_id = sourceEventId,
            payload = new { amount = 1 },
        };

        await PublishAsync(message);
        Guid firstEventId = await WaitForEventAsync(sourceEventId);

        await PublishAsync(message);
        // The second delivery re-resolves to the same idempotency key: give the processor a beat
        // to complete it, then assert no second Event was routed.
        await AssertQueueIsEmptyAsync();

        long eventCount = await QuerySingleAsync<long>(
            "SELECT COUNT(*) FROM events WHERE tenant_id=@TenantId AND source_event_id=@SourceEventId",
            new { TenantId = tenantId, SourceEventId = sourceEventId });
        Assert.Equal(1, eventCount);

        Guid onlyEventId = await QuerySingleAsync<Guid>(
            "SELECT id FROM events WHERE tenant_id=@TenantId AND source_event_id=@SourceEventId",
            new { TenantId = tenantId, SourceEventId = sourceEventId });
        Assert.Equal(firstEventId, onlyEventId);
    }

    private async Task PublishAsync(object body)
    {
        await using ServiceBusClient client = new(serviceBus.GetConnectionString());
        ServiceBusSender sender = client.CreateSender(QueueName);
        await sender.SendMessageAsync(new ServiceBusMessage(JsonSerializer.Serialize(body, HostJson.Options)));
    }

    private async Task<ServiceBusReceivedMessage?> ReceiveDeadLetterAsync()
    {
        await using ServiceBusClient client = new(serviceBus.GetConnectionString());
        ServiceBusReceiver receiver = client.CreateReceiver(
            QueueName, new ServiceBusReceiverOptions { SubQueue = SubQueue.DeadLetter });

        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            ServiceBusReceivedMessage? message = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(2));
            if (message is not null)
                return message;
        }

        return null;
    }

    private async Task AssertQueueIsEmptyAsync()
    {
        // Best-effort settle time: the processor runs concurrently with the test process.
        await Task.Delay(TimeSpan.FromSeconds(3));
        await using ServiceBusClient client = new(serviceBus.GetConnectionString());
        ServiceBusReceiver receiver = client.CreateReceiver(QueueName);
        ServiceBusReceivedMessage? leftover = await receiver.PeekMessageAsync();
        Assert.Null(leftover);
    }

    private async Task<Guid> WaitForEventAsync(string sourceEventId)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            Guid? eventId = await QuerySingleOrDefaultAsync<Guid?>(
                "SELECT id FROM events WHERE tenant_id=@TenantId AND source_event_id=@SourceEventId",
                new { TenantId = tenantId, SourceEventId = sourceEventId });
            if (eventId is { } id)
                return id;
            await Task.Delay(TimeSpan.FromMilliseconds(250));
        }

        throw new TimeoutException($"No Event appeared for source_event_id '{sourceEventId}' within 30s.");
    }

    private async Task<Guid> SeedAsync()
    {
        Guid tenantIdValue = Guid.NewGuid();
        Guid connectorId = Guid.NewGuid();
        Guid connectionId = Guid.NewGuid();
        Guid topicId = Guid.NewGuid();
        Guid sourceId = Guid.NewGuid();
        string manifest = TestConnectorManifest.Create(
            "sb_qualification_test", "SB Qualification Test", "source",
            declarativeSourceContract: true,
            sourceMappingExpression:
                "{ \"event_type\": event_type, \"source_event_id\": source_event_id, \"payload\": payload }");
        string sourceConfiguration = JsonSerializer.Serialize(new
        {
            source_contract = "event_json",
            transport = "azure_service_bus",
            @namespace = "sb-emulator",
            queue_name = QueueName,
            authentication = new { scheme = "connection_string", secret_ref = SecretReference },
        });

        await using DbConnection connection = database.CreateConnection();
        await connection.OpenAsync();
        await connection.ExecuteAsync($$$"""
            INSERT INTO tenants (id, slug, name, status, created_at, updated_at)
            VALUES (@TenantId, @Slug, 'SB Qualification', 'active', {{{database.Now}}}, {{{database.Now}}});

            INSERT INTO connectors (id, {{{database.KeyColumn}}}, contract_version, manifest_schema_version,
                name, direction, status, manifest, created_at, updated_at)
            VALUES (@ConnectorId, 'sb_qualification_test', 1, 1, 'SB Qualification Test', 'source',
                'active', {{{database.Json("@Manifest")}}}, {{{database.Now}}}, {{{database.Now}}});

            INSERT INTO connections (id, tenant_id, connector_id, name, config, status, created_at, updated_at)
            VALUES (@ConnectionId, @TenantId, @ConnectorId, 'sb-connection', {{{database.Json("@Config")}}},
                'active', {{{database.Now}}}, {{{database.Now}}});

            INSERT INTO topics (id, tenant_id, name, status, created_at, updated_at)
            VALUES (@TopicId, @TenantId, 'sb-topic', 'active', {{{database.Now}}}, {{{database.Now}}});

            INSERT INTO sources (id, tenant_id, connection_id, topic_id, type, configuration, status)
            VALUES (@SourceId, @TenantId, @ConnectionId, @TopicId, 'queue',
                {{{database.Json("@SourceConfiguration")}}}, 'active');
            """,
            new
            {
                TenantId = tenantIdValue,
                Slug = TenantSlug,
                ConnectorId = connectorId,
                Manifest = manifest,
                ConnectionId = connectionId,
                Config = "{}",
                TopicId = topicId,
                SourceId = sourceId,
                SourceConfiguration = sourceConfiguration,
            });

        return tenantIdValue;
    }

    private async Task<T> QuerySingleAsync<T>(string sql, object parameters)
    {
        await using DbConnection connection = database.CreateConnection();
        await connection.OpenAsync();
        return await connection.QuerySingleAsync<T>(sql, parameters);
    }

    private async Task<T?> QuerySingleOrDefaultAsync<T>(string sql, object parameters)
    {
        await using DbConnection connection = database.CreateConnection();
        await connection.OpenAsync();
        return await connection.QuerySingleOrDefaultAsync<T>(sql, parameters);
    }

    private sealed record EventRow(Guid TenantId, string EventType, string Status);
}
