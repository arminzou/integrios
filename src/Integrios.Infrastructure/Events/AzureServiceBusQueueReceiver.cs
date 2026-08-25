using System.Collections.Concurrent;
using System.Text.Json;
using Azure.Identity;
using Azure.Messaging.ServiceBus;
using Integrios.Application.Ingestion;
using Integrios.Application.Secrets;
using Integrios.Application.Telemetry;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Integrios.Infrastructure.Events;

// Read from configuration once, at registration, rather than injected as IConfiguration: the
// receiver's dependencies stay real ports, so the host-composition tests can build the container
// without standing up a configuration root.
internal sealed record QueueReconcileInterval(TimeSpan Value);

// One processor per active azure_service_bus queue Source, reconciled against the reader on an
// interval so control-plane changes take effect without an Ingestion restart. When no compatible
// Source exists, no ServiceBusClient is created, so an HTTP-only deployment needs no Azure
// credentials or running Azure client. Azure SDK types stay entirely inside this host-edge class;
// everything it hands to Application (tenant/topic/source ids, the parsed JSON body) is a plain
// CLR/JSON type.
internal sealed class AzureServiceBusQueueReceiver(
    IQueueSourceReader reader,
    ISourceVerificationSecretResolver secretResolver,
    IServiceProvider serviceProvider,
    QueueReconcileInterval reconcileInterval,
    IntegriosMetrics metrics,
    ILogger<AzureServiceBusQueueReceiver> logger)
    : BackgroundService
{
    // A broker that refuses the credential or cannot be reached does not fail StartProcessingAsync:
    // the client connects lazily, so the processor looks started and then errors forever. Counting
    // consecutive errors is what lets reconciliation notice and rebuild it.
    private const int UnhealthyErrorThreshold = 5;

    private readonly Dictionary<Guid, RunningProcessor> active = [];
    private readonly ConcurrentDictionary<Guid, int> consecutiveErrors = new();
    private readonly SemaphoreSlim lifecycleGate = new(1, 1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(reconcileInterval.Value);
        try
        {
            do
            {
                try
                {
                    await ReconcileAsync(stoppingToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // A failed pass must not end the loop: the reader query or one broker being
                    // unreachable is transient, and the next tick retries the whole desired state.
                    logger.LogError(exception, "Azure Service Bus queue Source reconciliation failed; retrying.");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
        }
    }

    // WebApplicationFactory<Program>-hosted tests can observe the hosted-service lifecycle more than
    // once against the same instance, and reconciliation itself runs concurrently with shutdown;
    // the gate is the single writer lock over `active`.
    private async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<ResolvedQueueSource> desired =
            await reader.ListActiveAzureServiceBusSourcesAsync(cancellationToken);
        Dictionary<Guid, ResolvedQueueSource> desiredById = desired.ToDictionary(source => source.SourceId);

        await lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            foreach (Guid sourceId in active.Keys.ToArray())
            {
                RunningProcessor running = active[sourceId];
                bool wanted = desiredById.TryGetValue(sourceId, out ResolvedQueueSource? source)
                    && source.Revision == running.Revision;
                bool unhealthy = consecutiveErrors.GetValueOrDefault(sourceId) >= UnhealthyErrorThreshold;
                if (wanted && !unhealthy)
                    continue;

                await StopProcessorAsync(running, cancellationToken);
                active.Remove(sourceId);
                consecutiveErrors.TryRemove(sourceId, out _);
                if (unhealthy && wanted)
                {
                    logger.LogWarning(
                        "Rebuilding Azure Service Bus processor for queue Source {SourceId} after {Count} consecutive errors.",
                        sourceId, UnhealthyErrorThreshold);
                }
                else
                {
                    logger.LogInformation("Stopped Azure Service Bus processor for queue Source {SourceId}.", sourceId);
                }
            }

            foreach (ResolvedQueueSource source in desired)
            {
                if (active.ContainsKey(source.SourceId))
                    continue;

                try
                {
                    active[source.SourceId] = await StartProcessorAsync(source, cancellationToken);
                    logger.LogInformation(
                        "Started Azure Service Bus processor for queue Source {SourceId}.", source.SourceId);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // One unusable Source (an unresolvable secret, a queue that does not exist) must
                    // not stop the others or the host; the next pass retries it.
                    logger.LogError(
                        exception,
                        "Could not start Azure Service Bus processor for queue Source {SourceId}; retrying.",
                        source.SourceId);
                }
            }
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    private async Task<RunningProcessor> StartProcessorAsync(
        ResolvedQueueSource source,
        CancellationToken cancellationToken)
    {
        ServiceBusClient client = await CreateClientAsync(source, cancellationToken);
        var options = new ServiceBusProcessorOptions
        {
            ReceiveMode = ServiceBusReceiveMode.PeekLock,
            AutoCompleteMessages = false,
            MaxConcurrentCalls = 1,
            PrefetchCount = 0,
        };
        // A topic subscription is received exactly like a queue - same processor, same settlement -
        // so only the entity address differs.
        ServiceBusProcessor processor = source.QueueName is { } queueName
            ? client.CreateProcessor(queueName, options)
            : client.CreateProcessor(source.ServiceBusTopicName, source.ServiceBusSubscriptionName, options);
        processor.ProcessMessageAsync += args => ProcessMessageAsync(source, args);
        processor.ProcessErrorAsync += args =>
        {
            int errors = consecutiveErrors.AddOrUpdate(source.SourceId, 1, (_, count) => count + 1);
            metrics.RecordQueueSourceError("azure_service_bus");
            logger.LogError(
                args.Exception,
                "Service Bus processor error for queue Source {SourceId} ({ErrorSource}); {Count} consecutive.",
                source.SourceId, args.ErrorSource, errors);
            return Task.CompletedTask;
        };

        try
        {
            await processor.StartProcessingAsync(cancellationToken);
        }
        catch
        {
            await processor.DisposeAsync();
            await client.DisposeAsync();
            throw;
        }

        return new RunningProcessor(source.Revision, client, processor);
    }

    private static async Task StopProcessorAsync(RunningProcessor running, CancellationToken cancellationToken)
    {
        await running.Processor.StopProcessingAsync(cancellationToken);
        await running.Processor.DisposeAsync();
        await running.Client.DisposeAsync();
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);

        await lifecycleGate.WaitAsync(CancellationToken.None);
        RunningProcessor[] snapshot;
        try
        {
            snapshot = [.. active.Values];
            active.Clear();
        }
        finally
        {
            lifecycleGate.Release();
        }

        foreach (RunningProcessor running in snapshot)
            await StopProcessorAsync(running, cancellationToken);
    }

    private sealed record RunningProcessor(string Revision, ServiceBusClient Client, ServiceBusProcessor Processor);

    private async Task<ServiceBusClient> CreateClientAsync(ResolvedQueueSource source, CancellationToken cancellationToken)
    {
        switch (source.Authentication.Scheme)
        {
            case "connection_string":
                string reference = source.Authentication.SecretReference
                    ?? throw new InvalidOperationException(
                        $"Queue Source {source.SourceId} connection_string authentication requires a secret reference.");
                string connectionString = await secretResolver.ResolveAsync(
                    new TenantSecretScope(source.TenantId, source.TenantSlug), reference, cancellationToken);
                return new ServiceBusClient(connectionString);
            case "azure_identity":
                return new ServiceBusClient(source.Namespace, new DefaultAzureCredential());
            default:
                throw new InvalidOperationException(
                    $"Unsupported queue authentication scheme '{source.Authentication.Scheme}'.");
        }
    }

    private async Task ProcessMessageAsync(ResolvedQueueSource source, ProcessMessageEventArgs args)
    {
        JsonElement rawInput;
        try
        {
            rawInput = JsonSerializer.Deserialize<JsonElement>(args.Message.Body);
            if (rawInput.ValueKind != JsonValueKind.Object)
                throw new JsonException("Message body must be a JSON object.");
        }
        catch (JsonException exception)
        {
            await DeadLetterAsync(args, "malformed_input", exception.Message);
            return;
        }

        // Each message gets its own DI scope: IEventAcceptance and friends are registered
        // singleton, but this keeps the processor callback aligned with how every other Ingestion
        // entry point (HTTP request scope) resolves its handler graph.
        await using AsyncServiceScope scope = serviceProvider.CreateAsyncScope();
        IMediator mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        try
        {
            await mediator.Send(
                new AcceptQueueMessageCommand(
                    source.TenantId, source.TopicId, source.SourceId,
                    source.SourceContractSchema, source.SourceMapping, rawInput),
                args.CancellationToken);
            await args.CompleteMessageAsync(args.Message, args.CancellationToken);
            consecutiveErrors.TryRemove(source.SourceId, out _);
        }
        catch (EventAcceptanceException exception)
        {
            // A deterministic Source rejection: dead-letter with a bounded, safe reason. Never the
            // complete payload or a secret.
            await DeadLetterAsync(args, "source_rejection", exception.Message);
        }
        catch (Exception exception)
        {
            // Transient or unexpected failure: abandon so the message stays redeliverable and
            // Service Bus remains the retry authority. A post-acceptance failure here (Complete
            // itself throwing) redelivers to the same already-accepted Event, which the shared
            // idempotency key resolves without another routing pass.
            logger.LogWarning(
                exception,
                "Unsettled failure processing Service Bus message for queue Source {SourceId}; abandoning for redelivery.",
                source.SourceId);
            await args.AbandonMessageAsync(args.Message, cancellationToken: args.CancellationToken);
        }
    }

    private static Task DeadLetterAsync(ProcessMessageEventArgs args, string reason, string description) =>
        args.DeadLetterMessageAsync(
            args.Message,
            deadLetterReason: reason,
            deadLetterErrorDescription: Bound(description),
            cancellationToken: args.CancellationToken);

    private static string Bound(string value) => value.Length > 256 ? value[..256] : value;
}
