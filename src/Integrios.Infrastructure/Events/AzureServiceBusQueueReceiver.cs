using System.Text.Json;
using Azure.Identity;
using Azure.Messaging.ServiceBus;
using Integrios.Application.Ingestion;
using Integrios.Application.Secrets;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Integrios.Infrastructure.Events;

// V1: one processor per active azure_service_bus queue Source, loaded once at startup and never
// dynamically reconciled. When no compatible Source exists, no ServiceBusClient is created, so an
// HTTP-only deployment needs no Azure credentials or running Azure client. Azure SDK types stay
// entirely inside this host-edge class; everything it hands to Application (tenant/topic/source ids,
// the parsed JSON body) is a plain CLR/JSON type.
internal sealed class AzureServiceBusQueueReceiver(
    IQueueSourceCatalog catalog,
    ISourceVerificationSecretResolver secretResolver,
    IServiceProvider serviceProvider,
    ILogger<AzureServiceBusQueueReceiver> logger)
    : IHostedService
{
    private readonly List<(ServiceBusClient Client, ServiceBusProcessor Processor)> active = [];
    private readonly SemaphoreSlim lifecycleGate = new(1, 1);

    // WebApplicationFactory<Program>-hosted tests can observe IHostedService.StartAsync/StopAsync
    // more than once against the same instance; guard against overlapping calls mutating `active`
    // concurrently (production hosting only ever calls each once, so this is pure defense).
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            IReadOnlyList<ResolvedQueueSource> sources =
                await catalog.ListActiveAzureServiceBusSourcesAsync(cancellationToken);

            foreach (ResolvedQueueSource source in sources)
            {
                ServiceBusClient client = await CreateClientAsync(source, cancellationToken);
                ServiceBusProcessor processor = client.CreateProcessor(source.QueueName, new ServiceBusProcessorOptions
                {
                    ReceiveMode = ServiceBusReceiveMode.PeekLock,
                    AutoCompleteMessages = false,
                    MaxConcurrentCalls = 1,
                    PrefetchCount = 0,
                });
                processor.ProcessMessageAsync += args => ProcessMessageAsync(source, args);
                processor.ProcessErrorAsync += args =>
                {
                    logger.LogError(
                        args.Exception,
                        "Service Bus processor error for queue Source {SourceId} ({ErrorSource}).",
                        source.SourceId, args.ErrorSource);
                    return Task.CompletedTask;
                };
                await processor.StartProcessingAsync(cancellationToken);
                active.Add((client, processor));
            }

            if (sources.Count > 0)
                logger.LogInformation("Started {Count} Azure Service Bus queue Source processor(s).", sources.Count);
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await lifecycleGate.WaitAsync(CancellationToken.None);
        (ServiceBusClient Client, ServiceBusProcessor Processor)[] snapshot;
        try
        {
            snapshot = [.. active];
            active.Clear();
        }
        finally
        {
            lifecycleGate.Release();
        }

        foreach ((ServiceBusClient client, ServiceBusProcessor processor) in snapshot)
        {
            await processor.StopProcessingAsync(cancellationToken);
            await processor.DisposeAsync();
            await client.DisposeAsync();
        }
    }

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
