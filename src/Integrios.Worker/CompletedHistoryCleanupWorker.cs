using System.Diagnostics;
using Integrios.Application.Delivery;

namespace Integrios.Worker;

internal sealed class CompletedHistoryCleanupWorker : BackgroundService
{
    private readonly Func<CancellationToken, Task<CompletedHistoryCleanupResult>> runSweep;
    private readonly ILogger<CompletedHistoryCleanupWorker> logger;
    private readonly IWorkerLoopDelay delay;

    public CompletedHistoryCleanupWorker(
        ICompletedHistoryCleanup cleanup,
        HistoryRetentionOptions options,
        ILogger<CompletedHistoryCleanupWorker> logger,
        IWorkerLoopDelay delay)
        : this(
            cancellationToken => cleanup.SweepAsync(
                options.Period,
                HistoryRetentionOptions.BatchSize,
                cancellationToken),
            logger,
            delay)
    {
    }

    internal CompletedHistoryCleanupWorker(
        Func<CancellationToken, Task<CompletedHistoryCleanupResult>> runSweep,
        ILogger<CompletedHistoryCleanupWorker> logger,
        IWorkerLoopDelay delay)
    {
        this.runSweep = runSweep;
        this.logger = logger;
        this.delay = delay;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => RunAsync(stoppingToken);

    internal async Task RunAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            long started = Stopwatch.GetTimestamp();
            try
            {
                CompletedHistoryCleanupResult result = await runSweep(stoppingToken);
                if (result.Acquired)
                {
                    logger.LogInformation(
                        "Completed-history retention sweep completed at cutoff {Cutoff}; deleted {DeletedEventCount} Events in {BatchCount} batches over {DurationMilliseconds} ms.",
                        result.Cutoff,
                        result.DeletedEventCount,
                        result.BatchCount,
                        (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(
                    "Completed-history retention sweep failed with {ExceptionType} after {DurationMilliseconds} ms; retrying at the next scheduled sweep.",
                    ex.GetType().Name,
                    (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }

            try
            {
                await delay.DelayAsync(HistoryRetentionOptions.SweepInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
