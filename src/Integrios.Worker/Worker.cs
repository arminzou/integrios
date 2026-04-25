using Integrios.Application.Outbox;
using MediatR;

namespace Integrios.Worker;

public sealed class OutboxWorker(IMediator mediator, ILogger<OutboxWorker> logger) : BackgroundService
{
    private const int BatchSize = 10;
    public const int MaxAttempts = 3;
    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("OutboxWorker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var processed = await mediator.Send(new ProcessOutboxBatchCommand(BatchSize, MaxAttempts), stoppingToken);
                if (processed == 0)
                    await Task.Delay(IdleDelay, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unhandled error in outbox poll loop. Retrying after delay.");
                await Task.Delay(IdleDelay, stoppingToken);
            }
        }

        logger.LogInformation("OutboxWorker stopped.");
    }

    public static TimeSpan CalculateBackoff(int attemptCount)
    {
        var exponent = Math.Min(attemptCount - 1, 10);
        return TimeSpan.FromSeconds(30) * Math.Pow(2, exponent);
    }
}
