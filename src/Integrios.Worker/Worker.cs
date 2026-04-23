using Integrios.Worker.Infrastructure.Data;
using Integrios.Worker.Infrastructure.Http;

namespace Integrios.Worker;

public sealed class OutboxWorker(
    IOutboxRepository outboxRepository,
    IRoutingRepository routingRepository,
    IDeliveryClient deliveryClient,
    ILogger<OutboxWorker> logger) : BackgroundService
{
    private const int BatchSize = 10;
    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("OutboxWorker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var processed = await ProcessBatchAsync(stoppingToken);
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

    internal async Task<int> ProcessBatchAsync(CancellationToken cancellationToken)
    {
        var rows = await outboxRepository.ClaimBatchAsync(BatchSize, cancellationToken);

        foreach (var row in rows)
            await ProcessRowAsync(row, cancellationToken);

        return rows.Count;
    }

    private async Task ProcessRowAsync(OutboxRow row, CancellationToken cancellationToken)
    {
        var ev = await outboxRepository.GetEventAsync(row.EventId, cancellationToken);
        if (ev is null)
        {
            logger.LogWarning("Outbox row {OutboxId} references missing event {EventId}. Marking processed.", row.Id, row.EventId);
            await outboxRepository.MarkProcessedAsync(row.Id, cancellationToken);
            return;
        }

        var pipelineId = await routingRepository.FindPipelineIdAsync(ev.TenantId, ev.EventType, cancellationToken);
        if (pipelineId is null)
        {
            logger.LogWarning("No active pipeline found for tenant {TenantId} and event type {EventType}. Skipping event {EventId}.",
                ev.TenantId, ev.EventType, ev.Id);
            await outboxRepository.MarkProcessedAsync(row.Id, cancellationToken);
            return;
        }

        var routes = await routingRepository.GetActiveRoutesAsync(pipelineId.Value, cancellationToken);
        var matchingRoutes = routes.Where(r => r.MatchEventTypes.Contains(ev.EventType, StringComparer.OrdinalIgnoreCase)).ToList();

        if (matchingRoutes.Count == 0)
        {
            logger.LogInformation("Event {EventId} (type={EventType}) matched pipeline {PipelineId} but no routes. Marking completed.",
                ev.Id, ev.EventType, pipelineId.Value);
            await outboxRepository.UpdateEventStatusAsync(ev.Id, "completed", pipelineId.Value, cancellationToken);
            await outboxRepository.MarkProcessedAsync(row.Id, cancellationToken);
            return;
        }

        var allSucceeded = true;
        foreach (var route in matchingRoutes)
        {
            if (string.IsNullOrWhiteSpace(route.DestinationUrl))
            {
                logger.LogWarning("Route {RouteId} ({RouteName}) has no destination URL. Skipping.", route.Id, route.Name);
                continue;
            }

            logger.LogInformation("Delivering event {EventId} via route {RouteName} to {Url}",
                ev.Id, route.Name, route.DestinationUrl);

            var result = await deliveryClient.DeliverAsync(route.DestinationUrl, ev.PayloadJson, cancellationToken);

            if (result.Succeeded)
            {
                logger.LogInformation("Delivered event {EventId} via route {RouteName} — HTTP {StatusCode}",
                    ev.Id, route.Name, result.StatusCode);
            }
            else
            {
                allSucceeded = false;
                logger.LogError("Failed to deliver event {EventId} via route {RouteName} — HTTP {StatusCode}: {Error}",
                    ev.Id, route.Name, result.StatusCode, result.Error);
            }
        }

        var finalStatus = allSucceeded ? "completed" : "failed";
        await outboxRepository.UpdateEventStatusAsync(ev.Id, finalStatus, pipelineId.Value, cancellationToken);
        await outboxRepository.MarkProcessedAsync(row.Id, cancellationToken);

        logger.LogInformation("Event {EventId} processing complete — status={Status}", ev.Id, finalStatus);
    }
}
