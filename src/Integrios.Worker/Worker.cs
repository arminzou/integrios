using Integrios.Application.Delivery;
using Integrios.Application.Outbox;
using MediatR;

namespace Integrios.Worker;

public sealed class OutboxWorker(IMediator mediator, ILogger<OutboxWorker> logger) : BackgroundService
{
    private const int FanoutBatchSize = 10;
    private const int DispatchBatchSize = 25;
    public const int MaxAttempts = 3;
    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("OutboxWorker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var fannedOut = await mediator.Send(new ProcessOutboxBatchCommand(FanoutBatchSize), stoppingToken);
                var dispatched = await mediator.Send(new DispatchSubscriptionDeliveriesCommand(DispatchBatchSize, MaxAttempts), stoppingToken);

                if (fannedOut == 0 && dispatched == 0)
                    await Task.Delay(IdleDelay, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unhandled error in worker loop. Retrying after delay.");
                await Task.Delay(IdleDelay, stoppingToken);
            }
        }

        logger.LogInformation("OutboxWorker stopped.");
    }
}
