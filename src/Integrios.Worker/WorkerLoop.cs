namespace Integrios.Worker;

internal interface IWorkerLoopDelay
{
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

internal sealed class WorkerLoopDelay : IWorkerLoopDelay
{
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        Task.Delay(delay, cancellationToken);
}

internal static class WorkerLoop
{
    internal static async Task RunAsync(
        string name,
        Func<CancellationToken, Task<int>> runBatch,
        TimeSpan idlePollInterval,
        IWorkerLoopDelay delay,
        ILogger logger,
        CancellationToken stoppingToken)
    {
        logger.LogInformation("{WorkerLoop} started.", name);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                int processed = await runBatch(stoppingToken);
                if (processed == 0)
                    await delay.DelayAsync(idlePollInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unhandled error in {WorkerLoop}. Retrying after delay.", name);

                try
                {
                    await delay.DelayAsync(idlePollInterval, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }

        logger.LogInformation("{WorkerLoop} stopped.", name);
    }
}
