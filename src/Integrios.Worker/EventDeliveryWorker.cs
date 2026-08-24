using Integrios.Application.Delivery;
using MediatR;

namespace Integrios.Worker;

internal sealed class EventDeliveryWorker : BackgroundService
{
    private readonly Func<CancellationToken, Task<int>> runBatch;
    private readonly ILogger<EventDeliveryWorker> logger;
    private readonly DeliveryLoopOptions options;
    private readonly IWorkerLoopDelay delay;

    public EventDeliveryWorker(
        ISender sender,
        ILogger<EventDeliveryWorker> logger,
        DeliveryLoopOptions options,
        IWorkerLoopDelay delay)
        : this(
            cancellationToken => sender.Send(
                new DispatchEventDeliveriesCommand(options.BatchSize),
                cancellationToken),
            logger,
            options,
            delay)
    {
    }

    internal EventDeliveryWorker(
        Func<CancellationToken, Task<int>> runBatch,
        ILogger<EventDeliveryWorker> logger,
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
            nameof(EventDeliveryWorker),
            runBatch,
            options.IdlePollInterval,
            delay,
            logger,
            stoppingToken);
}
