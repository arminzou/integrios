extern alias IngestionHost;

using System.Collections.Concurrent;
using System.Data.Common;
using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Dapper;
using Integrios.Application.Ingestion;
using Integrios.Tests.Shared;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.ServiceBus;

namespace Integrios.Application.FunctionalTests.Ingestion;

// Real-broker integration: proves PeekLock completion, deterministic dead-lettering, transient
// redelivery, and idempotent duplicate handling against the official Azure Service Bus emulator.
public sealed class AzureServiceBusQueueSourceIntegrationTests(AzureServiceBusQueueSourceFixture fixture)
    : IClassFixture<AzureServiceBusQueueSourceFixture>
{
    internal const string TenantSlug = "sb-integration";
    internal const string SecretReference = "sb_connection_string";
    internal const string QueueName = "queue.1";

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
        Assert.Equal(fixture.TenantId, row.TenantId);
        Assert.Equal("order.created", row.EventType);
        Assert.Equal("accepted", row.Status);

        // Complete removes it from the active queue; nothing should be left to redeliver.
        await AssertQueueIsEmptyAsync();
    }

    [Fact]
    public async Task InvalidNormalizedOutput_DeadLettersWithoutCreatingEvent()
    {
        string sourceEventId = $"evt-{Guid.NewGuid():N}";
        // A missing event_type makes the normalized Source-contract output invalid.
        await PublishAsync(new { source_event_id = sourceEventId, payload = new { } });

        await using ServiceBusClient client = new(fixture.ServiceBus.GetConnectionString());
        await using ServiceBusReceiver receiver = client.CreateReceiver(
            QueueName, new ServiceBusReceiverOptions { SubQueue = SubQueue.DeadLetter });
        ServiceBusReceivedMessage? deadLettered = await ReceiveDeadLetterAsync(receiver);
        Assert.NotNull(deadLettered);
        Assert.Equal("source_rejection", deadLettered!.DeadLetterReason);

        long eventCount = await QuerySingleAsync<long>(
            "SELECT COUNT(*) FROM events WHERE tenant_id=@TenantId AND source_event_id=@SourceEventId",
            new { fixture.TenantId, SourceEventId = sourceEventId });
        Assert.Equal(0, eventCount);
        await receiver.CompleteMessageAsync(deadLettered);
    }

    [Fact]
    public async Task TransientAcceptanceFailure_AbandonsAndRedeliversMessage()
    {
        string sourceEventId = $"evt-{Guid.NewGuid():N}";
        fixture.EventAcceptance.FailNext(sourceEventId);

        await PublishAsync(new
        {
            event_type = "order.created",
            source_event_id = sourceEventId,
            payload = new { amount = 4200 },
        });

        await WaitForEventAsync(sourceEventId);

        Assert.Equal(2, fixture.EventAcceptance.AttemptsFor(sourceEventId));
        await AssertQueueIsEmptyAsync();
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
            new { fixture.TenantId, SourceEventId = sourceEventId });
        Assert.Equal(1, eventCount);

        Guid onlyEventId = await QuerySingleAsync<Guid>(
            "SELECT id FROM events WHERE tenant_id=@TenantId AND source_event_id=@SourceEventId",
            new { fixture.TenantId, SourceEventId = sourceEventId });
        Assert.Equal(firstEventId, onlyEventId);
    }

    private async Task PublishAsync(object body)
    {
        await using ServiceBusClient client = new(fixture.ServiceBus.GetConnectionString());
        ServiceBusSender sender = client.CreateSender(QueueName);
        await sender.SendMessageAsync(new ServiceBusMessage(JsonSerializer.Serialize(body, HostJson.Options)));
    }

    private static async Task<ServiceBusReceivedMessage?> ReceiveDeadLetterAsync(ServiceBusReceiver receiver)
    {
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
        await using ServiceBusClient client = new(fixture.ServiceBus.GetConnectionString());
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        ServiceBusReceivedMessage? leftover = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            await using ServiceBusReceiver receiver = client.CreateReceiver(QueueName);
            leftover = await receiver.PeekMessageAsync();
            if (leftover is null)
                return;
            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }

        Assert.Null(leftover);
    }

    private async Task<Guid> WaitForEventAsync(string sourceEventId)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            Guid? eventId = await QuerySingleOrDefaultAsync<Guid?>(
                "SELECT id FROM events WHERE tenant_id=@TenantId AND source_event_id=@SourceEventId",
                new { fixture.TenantId, SourceEventId = sourceEventId });
            if (eventId is { } id)
                return id;
            await Task.Delay(TimeSpan.FromMilliseconds(250));
        }

        throw new TimeoutException($"No Event appeared for source_event_id '{sourceEventId}' within 30s.");
    }

    internal static async Task<Guid> SeedAsync(FunctionalDatabase database)
    {
        Guid tenantIdValue = Guid.NewGuid();
        Guid connectorId = Guid.NewGuid();
        Guid connectionId = Guid.NewGuid();
        Guid topicId = Guid.NewGuid();
        Guid sourceId = Guid.NewGuid();
        string manifest = TestConnectorManifest.Create(
            "sb_integration_test", "SB Integration Test", "source",
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
            VALUES (@TenantId, @Slug, 'SB Integration', 'active', {{{database.Now}}}, {{{database.Now}}});

            INSERT INTO connectors (id, {{{database.KeyColumn}}}, contract_version, manifest_schema_version,
                name, direction, status, manifest, created_at, updated_at)
            VALUES (@ConnectorId, 'sb_integration_test', 1, 1, 'SB Integration Test', 'source',
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
        await using DbConnection connection = fixture.Database.CreateConnection();
        await connection.OpenAsync();
        return await connection.QuerySingleAsync<T>(sql, parameters);
    }

    private async Task<T?> QuerySingleOrDefaultAsync<T>(string sql, object parameters)
    {
        await using DbConnection connection = fixture.Database.CreateConnection();
        await connection.OpenAsync();
        return await connection.QuerySingleOrDefaultAsync<T>(sql, parameters);
    }

    private sealed record EventRow(Guid TenantId, string EventType, string Status);
}

public sealed class AzureServiceBusQueueSourceFixture : IAsyncLifetime
{
    private const string EmulatorImage =
        "mcr.microsoft.com/azure-messaging/servicebus-emulator@sha256:5a96d893b245031740f7d46e0fe5ff282d24b78c4b7d761dd57590f3f010a9b3";

    internal FunctionalDatabase Database { get; } = new();
    internal ServiceBusContainer ServiceBus { get; } = new ServiceBusBuilder(EmulatorImage)
        .WithAcceptLicenseAgreement(true)
        .Build();
    internal FaultInjectingEventAcceptance EventAcceptance { get; private set; } = null!;
    internal Guid TenantId { get; private set; }

    private WebApplicationFactory<IngestionHost::Program> factory = null!;

    public async Task InitializeAsync()
    {
        await Database.StartAsync();
        await ServiceBus.StartAsync();
        TenantId = await AzureServiceBusQueueSourceIntegrationTests.SeedAsync(Database);

        factory = new WebApplicationFactory<IngestionHost::Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Integrios:SourceSecrets:Provider", "configuration");
            builder.UseSetting("Database:Provider", Database.Provider);
            builder.UseSetting($"ConnectionStrings:{Database.ConnectionName}", Database.ConnectionString);
            builder.ConfigureAppConfiguration((_, config) => config.AddConfiguration(Database.Configuration));
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    [$"SourceSecrets:{AzureServiceBusQueueSourceIntegrationTests.TenantSlug}:{AzureServiceBusQueueSourceIntegrationTests.SecretReference}"] =
                        ServiceBus.GetConnectionString(),
                }));
            builder.ConfigureServices(services =>
            {
                ServiceDescriptor registration = services.Single(service => service.ServiceType == typeof(IEventAcceptance));
                services.Remove(registration);
                services.AddSingleton(provider => new FaultInjectingEventAcceptance(
                    (IEventAcceptance)ActivatorUtilities.CreateInstance(provider, registration.ImplementationType!)));
                services.AddSingleton<IEventAcceptance>(provider =>
                    provider.GetRequiredService<FaultInjectingEventAcceptance>());
            });
        });

        // Force lazy host construction so the queue processor is connected before publication.
        using HttpClient warmup = factory.CreateClient();
        using HttpResponseMessage response = await warmup.GetAsync("/health");
        response.EnsureSuccessStatusCode();
        EventAcceptance = factory.Services.GetRequiredService<FaultInjectingEventAcceptance>();
    }

    public async Task DisposeAsync()
    {
        factory.Dispose();
        await ServiceBus.DisposeAsync();
        await Database.DisposeAsync();
    }
}

internal sealed class FaultInjectingEventAcceptance(IEventAcceptance inner) : IEventAcceptance
{
    private readonly ConcurrentDictionary<string, int> attempts = new();
    private readonly ConcurrentDictionary<string, byte> failures = new();

    internal void FailNext(string sourceEventId) => failures[sourceEventId] = 0;

    internal int AttemptsFor(string sourceEventId) => attempts.GetValueOrDefault(sourceEventId);

    public Task<EventAcceptance> AcceptAsync(
        EventSubmission submission,
        string? traceparent,
        CancellationToken cancellationToken)
    {
        string sourceEventId = submission.SourceEventId ?? string.Empty;
        attempts.AddOrUpdate(sourceEventId, 1, (_, count) => count + 1);
        if (failures.TryRemove(sourceEventId, out _))
            throw new InvalidOperationException("Injected transient acceptance failure.");

        return inner.AcceptAsync(submission, traceparent, cancellationToken);
    }
}
