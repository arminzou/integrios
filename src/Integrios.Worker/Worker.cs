using Integrios.Application.Delivery;
using Integrios.Application.Outbox;
using MediatR;

namespace Integrios.Worker;

public sealed class OutboxWorker(
    IMediator mediator,
    ILogger<OutboxWorker> logger,
    DeliveryExecutionOptions options) : BackgroundService
{
    private const int FanoutBatchSize = 10;
    private const int DispatchBatchSize = 25;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("OutboxWorker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var fannedOut = await mediator.Send(new ProcessOutboxBatchCommand(FanoutBatchSize), stoppingToken);
                var dispatched = await mediator.Send(new DispatchSubscriptionDeliveriesCommand(DispatchBatchSize), stoppingToken);

                if (fannedOut == 0 && dispatched == 0)
                    await Task.Delay(options.IdlePollInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unhandled error in worker loop. Retrying after delay.");
                await Task.Delay(options.IdlePollInterval, stoppingToken);
            }
        }

        logger.LogInformation("OutboxWorker stopped.");
    }
}
