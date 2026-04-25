using Integrios.Application.Abstractions;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Integrios.Application.Outbox;

public sealed record ProcessOutboxBatchCommand(int BatchSize, int MaxAttempts) : IRequest<int>;

internal sealed class ProcessOutboxBatchCommandHandler(
    IOutboxRepository outboxRepository,
    IRoutingRepository routingRepository,
    IDeliveryAttemptRepository deliveryAttemptRepository,
    IDeliveryClient deliveryClient,
    ILogger<ProcessOutboxBatchCommandHandler> logger) : IRequestHandler<ProcessOutboxBatchCommand, int>
{
    private static readonly TimeSpan RetryBaseDelay = TimeSpan.FromSeconds(30);

    public async Task<int> Handle(ProcessOutboxBatchCommand command, CancellationToken cancellationToken)
    {
        var rows = await outboxRepository.ClaimBatchAsync(command.BatchSize, cancellationToken);

        foreach (var row in rows)
            await ProcessRowAsync(row, command.MaxAttempts, cancellationToken);

        return rows.Count;
    }

    private async Task ProcessRowAsync(OutboxRow row, int maxAttempts, CancellationToken cancellationToken)
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
            logger.LogWarning("No active pipeline for tenant {TenantId} / event type {EventType}. Skipping event {EventId}.",
                ev.TenantId, ev.EventType, ev.Id);
            await outboxRepository.MarkProcessedAsync(row.Id, cancellationToken);
            return;
        }

        var routes = await routingRepository.GetActiveRoutesAsync(pipelineId.Value, cancellationToken);
        var matchingRoutes = routes
            .Where(r => r.MatchEventTypes.Contains(ev.EventType, StringComparer.OrdinalIgnoreCase))
            .ToList();

        if (matchingRoutes.Count == 0)
        {
            logger.LogInformation("Event {EventId} matched pipeline {PipelineId} but no routes. Marking completed.", ev.Id, pipelineId.Value);
            await outboxRepository.UpdateEventStatusAsync(ev.Id, "completed", pipelineId.Value, cancellationToken);
            await outboxRepository.MarkProcessedAsync(row.Id, cancellationToken);
            return;
        }

        var allSucceeded = true;
        foreach (var route in matchingRoutes)
        {
            if (string.IsNullOrWhiteSpace(route.DestinationUrl))
            {
                logger.LogWarning("Route {RouteId} has no destination URL. Skipping.", route.Id);
                continue;
            }

            var startedAt = DateTimeOffset.UtcNow;
            var attemptNumber = await deliveryAttemptRepository.GetAttemptCountAsync(ev.Id, route.Id, cancellationToken) + 1;

            logger.LogInformation("Delivering event {EventId} via route {RouteName} to {Url} (attempt {N})",
                ev.Id, route.Name, route.DestinationUrl, attemptNumber);

            var result = await deliveryClient.DeliverAsync(route.DestinationUrl, ev.PayloadJson, cancellationToken);
            var completedAt = DateTimeOffset.UtcNow;

            await deliveryAttemptRepository.RecordAsync(
                eventId: ev.Id,
                routeId: route.Id,
                destinationConnectionId: route.DestinationConnectionId,
                attemptNumber: attemptNumber,
                status: result.Succeeded ? "succeeded" : "failed",
                requestPayloadJson: ev.PayloadJson,
                responseStatusCode: result.StatusCode > 0 ? result.StatusCode : null,
                responseBody: null,
                errorMessage: result.Error,
                startedAt: startedAt,
                completedAt: completedAt,
                cancellationToken: cancellationToken);

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

        if (allSucceeded)
        {
            await outboxRepository.UpdateEventStatusAsync(ev.Id, "completed", pipelineId.Value, cancellationToken);
            await outboxRepository.MarkProcessedAsync(row.Id, cancellationToken);
            logger.LogInformation("Event {EventId} processing complete — status=completed", ev.Id);
        }
        else
        {
            var newAttemptCount = row.AttemptCount + 1;
            if (newAttemptCount >= maxAttempts)
            {
                await outboxRepository.UpdateEventStatusAsync(ev.Id, "dead_lettered", pipelineId.Value, cancellationToken);
                await outboxRepository.MarkProcessedAsync(row.Id, cancellationToken);
                logger.LogError("Event {EventId} dead-lettered after {AttemptCount} attempts.", ev.Id, newAttemptCount);
            }
            else
            {
                var deliverAfter = DateTimeOffset.UtcNow + CalculateBackoff(newAttemptCount);
                await outboxRepository.ScheduleRetryAsync(row.Id, newAttemptCount, deliverAfter, cancellationToken);
                logger.LogWarning("Event {EventId} delivery failed. Scheduled retry {AttemptCount} at {DeliverAfter}.",
                    ev.Id, newAttemptCount, deliverAfter);
            }
        }
    }

    internal static TimeSpan CalculateBackoff(int attemptCount)
    {
        var exponent = Math.Min(attemptCount - 1, 10);
        return RetryBaseDelay * Math.Pow(2, exponent);
    }
}
