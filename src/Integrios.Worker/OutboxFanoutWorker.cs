using Integrios.Application.Delivery;
using MediatR;

namespace Integrios.Worker;

internal sealed class OutboxFanoutWorker : BackgroundService
{
    private readonly Func<CancellationToken, Task<int>> runBatch;
    private readonly ILogger<OutboxFanoutWorker> logger;
    private readonly FanoutLoopOptions options;
    private readonly IWorkerLoopDelay delay;

    public OutboxFanoutWorker(
        ISender sender,
        ILogger<OutboxFanoutWorker> logger,
        FanoutLoopOptions options,
        IWorkerLoopDelay delay)
        : this(
            cancellationToken => sender.Send(new ProcessOutboxBatchCommand(options.BatchSize), cancellationToken),
            logger,
            options,
            delay)
    {
    }

    internal OutboxFanoutWorker(
        Func<CancellationToken, Task<int>> runBatch,
        ILogger<OutboxFanoutWorker> logger,
        FanoutLoopOptions options,
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
            nameof(OutboxFanoutWorker),
            runBatch,
            options.IdlePollInterval,
            delay,
            logger,
            stoppingToken);
}
