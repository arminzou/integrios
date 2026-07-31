using Integrios.Application.Delivery;
using MediatR;

namespace Integrios.Worker;

internal sealed class SubscriptionDeliveryWorker : BackgroundService
{
    private readonly Func<CancellationToken, Task<int>> runBatch;
    private readonly ILogger<SubscriptionDeliveryWorker> logger;
    private readonly DeliveryLoopOptions options;
    private readonly IWorkerLoopDelay delay;

    public SubscriptionDeliveryWorker(
        ISender sender,
        ILogger<SubscriptionDeliveryWorker> logger,
        DeliveryLoopOptions options,
        IWorkerLoopDelay delay)
        : this(
            cancellationToken => sender.Send(
                new DispatchSubscriptionDeliveriesCommand(options.BatchSize),
                cancellationToken),
            logger,
            options,
            delay)
    {
    }

    internal SubscriptionDeliveryWorker(
        Func<CancellationToken, Task<int>> runBatch,
        ILogger<SubscriptionDeliveryWorker> logger,
        DeliveryLoopOptions options,
        IWorkerLoopDelay delay)
    {
        this.runBatch = runBatch;
        this.logger = logger;
        this.options = options;
        this.delay = delay;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => RunAsync(stoppingToken);

    internal Task RunAsync(CancellationToken stoppingToken) =>
        WorkerLoop.RunAsync(
            nameof(SubscriptionDeliveryWorker),
            runBatch,
            options.IdlePollInterval,
            delay,
            logger,
            stoppingToken);
}
