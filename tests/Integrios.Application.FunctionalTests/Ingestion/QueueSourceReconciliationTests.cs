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

// Queue processors follow the control plane while Ingestion keeps running: a Source created after
// startup begins consuming, a revoked one stops, and a change to the resolved configuration
// recycles that Source's processor. The emulator image ships exactly one queue, so these prove
// reconciliation through Source lifecycle and Connector manifest changes rather than by moving a
// Source between queues.
public sealed class QueueSourceReconciliationTests(QueueSourceReconciliationFixture fixture)
    : IClassFixture<QueueSourceReconciliationFixture>, IAsyncLifetime
{
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(30);

    // These are the only tests here that assert an absence, which cannot be waited on directly: the
    // best available signal is "nothing happened for long enough". Both windows are deliberately
    // generous relative to the one-second reconcile interval, because the tier runs alongside
    // database and broker containers and a loaded machine can delay a pass well past its schedule.
    private static readonly TimeSpan ReconcileSettled = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan NotConsumed = TimeSpan.FromSeconds(20);

    private readonly Guid sourceId = Guid.NewGuid();

    public Task InitializeAsync() => Task.CompletedTask;

    // Revoking stops this test's processor, so the next test starts against an idle broker; the
    // drain has to follow that stop, or the still-running processor consumes what it drains.
    public async Task DisposeAsync()
    {
        await fixture.RevokeSourceAsync(sourceId);
        await Task.Delay(ReconcileSettled);
        await fixture.DrainQueueAsync();
    }

    [Fact]
    public async Task SourceCreatedAfterStartup_BeginsConsumingWithoutRestart()
    {
        await fixture.CreateSourceAsync(sourceId);

        string sourceEventId = $"evt-{Guid.NewGuid():N}";
        await fixture.PublishAsync(new
        {
            event_type = "order.created",
            source_event_id = sourceEventId,
            payload = new { amount = 4200 },
        });

        Guid? eventId = await fixture.WaitForEventAsync(sourceEventId, Settle);
        Assert.NotNull(eventId);

        Guid attributedSource = await fixture.QuerySingleAsync<Guid>(
            "SELECT source_id FROM events WHERE id=@Id",
            new { Id = eventId!.Value });
        Assert.Equal(sourceId, attributedSource);
    }

    [Fact]
    public async Task SourceRevokedAfterStartup_StopsConsuming()
    {
        await fixture.CreateSourceAsync(sourceId);
        string consumed = $"evt-{Guid.NewGuid():N}";
        await fixture.PublishAsync(new { event_type = "order.created", source_event_id = consumed, payload = new { } });
        Assert.NotNull(await fixture.WaitForEventAsync(consumed, Settle));

        await fixture.RevokeSourceAsync(sourceId);
        await Task.Delay(ReconcileSettled);

        string ignored = $"evt-{Guid.NewGuid():N}";
        await fixture.PublishAsync(new { event_type = "order.created", source_event_id = ignored, payload = new { } });

        Assert.Null(await fixture.WaitForEventAsync(ignored, NotConsumed));
        Assert.NotNull(await fixture.PeekAsync());
    }

    // The revision key, not just add/remove: the Source is active throughout and only its
    // configuration changes. Without a revision the processor keeps the queue it bound to when it
    // started. Repointing at a queue the broker does not have also proves the second half — a
    // processor that cannot start is retried rather than being fatal, so restoring the
    // configuration brings consumption back.
    [Fact]
    public async Task SourceConfigurationChanged_RebindsTheProcessor()
    {
        await fixture.CreateSourceAsync(sourceId);
        string before = $"evt-{Guid.NewGuid():N}";
        await fixture.PublishAsync(new { event_type = "order.created", source_event_id = before, payload = new { } });
        Assert.NotNull(await fixture.WaitForEventAsync(before, Settle));

        await fixture.SetQueueNameAsync(sourceId, "queue.404");
        await Task.Delay(ReconcileSettled);

        string whileRebound = $"evt-{Guid.NewGuid():N}";
        await fixture.PublishAsync(new { event_type = "order.created", source_event_id = whileRebound, payload = new { } });
        Assert.Null(await fixture.WaitForEventAsync(whileRebound, NotConsumed));

        await fixture.SetQueueNameAsync(sourceId, AzureServiceBusSourceIntegrationTests.QueueName);
        Assert.NotNull(await fixture.WaitForEventAsync(whileRebound, Settle));
    }
}

public sealed class QueueSourceReconciliationFixture : IAsyncLifetime
{
    private const string EmulatorImage =
        "mcr.microsoft.com/azure-messaging/servicebus-emulator@sha256:5a96d893b245031740f7d46e0fe5ff282d24b78c4b7d761dd57590f3f010a9b3";

    internal TimeSpan ReconcileInterval { get; } = TimeSpan.FromSeconds(1);
    internal FunctionalDatabase Database { get; } = new();
    internal ServiceBusContainer ServiceBus { get; } = new ServiceBusBuilder(EmulatorImage)
        .WithAcceptLicenseAgreement(true)
        .Build();
    internal SeededQueueSource Seeded { get; private set; } = null!;

    private WebApplicationFactory<IngestionHost::Program> factory = null!;

    public async Task InitializeAsync()
    {
        await Database.StartAsync();
        await ServiceBus.StartAsync();
        Seeded = await AzureServiceBusSourceIntegrationTests.SeedAsync(Database, includeSource: false);

        factory = new WebApplicationFactory<IngestionHost::Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Integrios:SourceSecrets:Provider", "configuration");
            builder.UseSetting("Integrios:QueueSources:ReconcileSeconds", "1");
            builder.UseSetting("Database:Provider", Database.Provider);
            builder.UseSetting($"ConnectionStrings:{Database.ConnectionName}", Database.ConnectionString);
            builder.ConfigureAppConfiguration((_, config) => config.AddConfiguration(Database.Configuration));
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    [$"SourceSecrets:{AzureServiceBusSourceIntegrationTests.TenantSlug}:{AzureServiceBusSourceIntegrationTests.SecretReference}"] =
                        ServiceBus.GetConnectionString(),
                }));
        });

        using HttpClient warmup = factory.CreateClient();
        using HttpResponseMessage response = await warmup.GetAsync("/health");
        response.EnsureSuccessStatusCode();
    }

    public async Task DisposeAsync()
    {
        factory.Dispose();
        await ServiceBus.DisposeAsync();
        await Database.DisposeAsync();
    }

    internal Task CreateSourceAsync(Guid sourceId) =>
        AzureServiceBusSourceIntegrationTests.InsertQueueSourceAsync(Database, Seeded, sourceId);

    internal Task RevokeSourceAsync(Guid sourceId) => ExecuteAsync(
        $"UPDATE sources SET status = 'revoked', revoked_at = {Database.Now} WHERE id = @Id",
        new { Id = sourceId });

    internal Task SetQueueNameAsync(Guid sourceId, string queueName)
    {
        string configuration = JsonSerializer.Serialize(new
        {
            source_contract = "event_json",
            transport = "azure_service_bus",
            authentication = new
            {
                scheme = "connection_string",
                secret_ref = AzureServiceBusSourceIntegrationTests.SecretReference,
            },
            transport_config = new { @namespace = "sb-emulator", queue_name = queueName },
        });
        return ExecuteAsync(
            $"UPDATE sources SET configuration = {Database.Json("@Configuration")} WHERE id = @Id",
            new { Configuration = configuration, Id = sourceId });
    }

    internal async Task PublishAsync(object body)
    {
        await using ServiceBusClient client = new(ServiceBus.GetConnectionString());
        ServiceBusSender sender = client.CreateSender(AzureServiceBusSourceIntegrationTests.QueueName);
        await sender.SendMessageAsync(new ServiceBusMessage(JsonSerializer.Serialize(body, HostJson.Options)));
    }

    internal async Task DrainQueueAsync()
    {
        await using ServiceBusClient client = new(ServiceBus.GetConnectionString());
        await using ServiceBusReceiver receiver = client.CreateReceiver(
            AzureServiceBusSourceIntegrationTests.QueueName);
        while (await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(1)) is { } message)
            await receiver.CompleteMessageAsync(message);
    }

    internal async Task<ServiceBusReceivedMessage?> PeekAsync()
    {
        await using ServiceBusClient client = new(ServiceBus.GetConnectionString());
        await using ServiceBusReceiver receiver = client.CreateReceiver(
            AzureServiceBusSourceIntegrationTests.QueueName);
        return await receiver.PeekMessageAsync();
    }

    internal async Task<Guid?> WaitForEventAsync(string sourceEventId, TimeSpan timeout)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            Guid? eventId = await QuerySingleOrDefaultAsync<Guid?>(
                "SELECT id FROM events WHERE tenant_id=@TenantId AND source_event_id=@SourceEventId",
                new { Seeded.TenantId, SourceEventId = sourceEventId });
            if (eventId is { } id)
                return id;
            await Task.Delay(TimeSpan.FromMilliseconds(250));
        }

        return null;
    }

    internal Task<string> EventTypeAsync(Guid eventId) =>
        QuerySingleAsync<string>("SELECT event_type FROM events WHERE id=@Id", new { Id = eventId });

    internal async Task<T> QuerySingleAsync<T>(string sql, object parameters)
    {
        await using DbConnection connection = Database.CreateConnection();
        await connection.OpenAsync();
        return await connection.QuerySingleAsync<T>(sql, parameters);
    }

    private async Task<T?> QuerySingleOrDefaultAsync<T>(string sql, object parameters)
    {
        await using DbConnection connection = Database.CreateConnection();
        await connection.OpenAsync();
        return await connection.QuerySingleOrDefaultAsync<T>(sql, parameters);
    }

    private async Task ExecuteAsync(string sql, object parameters)
    {
        await using DbConnection connection = Database.CreateConnection();
        await connection.OpenAsync();
        await connection.ExecuteAsync(sql, parameters);
    }
}
