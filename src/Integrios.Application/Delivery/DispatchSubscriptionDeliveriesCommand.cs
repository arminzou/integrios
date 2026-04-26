using Integrios.Application.Abstractions;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Integrios.Application.Delivery;

public sealed record DispatchSubscriptionDeliveriesCommand(int BatchSize, int MaxAttempts) : IRequest<int>;

internal sealed class DispatchSubscriptionDeliveriesCommandHandler(
    ISubscriptionDeliveryRepository deliveryRepository,
    IDeliveryAttemptRepository attemptRepository,
    IDeliveryClient deliveryClient,
    ILogger<DispatchSubscriptionDeliveriesCommandHandler> logger) : IRequestHandler<DispatchSubscriptionDeliveriesCommand, int>
{
    private static readonly TimeSpan RetryBaseDelay = TimeSpan.FromSeconds(30);

    public async Task<int> Handle(DispatchSubscriptionDeliveriesCommand command, CancellationToken cancellationToken)
    {
        var rows = await deliveryRepository.ClaimBatchAsync(command.BatchSize, cancellationToken);

        foreach (var row in rows)
            await DispatchAsync(row, command.MaxAttempts, cancellationToken);

        return rows.Count;
    }

    private async Task DispatchAsync(SubscriptionDeliveryWorkItem row, int maxAttempts, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(row.DestinationUrl))
        {
            logger.LogWarning("Subscription {SubscriptionId} has no destination URL. Dead-lettering delivery {DeliveryId}.",
                row.SubscriptionId, row.Id);
            await deliveryRepository.MarkDeadLetteredAsync(row.Id, cancellationToken);
            return;
        }

        var attemptNumber = row.AttemptCount + 1;
        var startedAt = DateTimeOffset.UtcNow;

        logger.LogInformation("Dispatching delivery {DeliveryId} (event {EventId} → subscription {SubscriptionId}, attempt {N}) to {Url}",
            row.Id, row.EventId, row.SubscriptionId, attemptNumber, row.DestinationUrl);

        var result = await deliveryClient.DeliverAsync(row.DestinationUrl, row.PayloadJson, cancellationToken);
        var completedAt = DateTimeOffset.UtcNow;

        await attemptRepository.RecordAsync(
            eventId: row.EventId,
            subscriptionId: row.SubscriptionId,
            destinationConnectionId: row.DestinationConnectionId,
            attemptNumber: attemptNumber,
            status: result.Succeeded ? "succeeded" : "failed",
            requestPayloadJson: row.PayloadJson,
            responseStatusCode: result.StatusCode > 0 ? result.StatusCode : null,
            responseBody: null,
            errorMessage: result.Error,
            startedAt: startedAt,
            completedAt: completedAt,
            cancellationToken: cancellationToken);

        if (result.Succeeded)
        {
            await deliveryRepository.MarkSucceededAsync(row.Id, cancellationToken);
            logger.LogInformation("Delivery {DeliveryId} succeeded — HTTP {StatusCode}", row.Id, result.StatusCode);
            return;
        }

        if (attemptNumber >= maxAttempts)
        {
            await deliveryRepository.MarkDeadLetteredAsync(row.Id, cancellationToken);
            logger.LogError("Delivery {DeliveryId} dead-lettered after {AttemptCount} attempt(s). Last error: {Error}",
                row.Id, attemptNumber, result.Error);
            return;
        }

        var deliverAfter = DateTimeOffset.UtcNow + CalculateBackoff(attemptNumber);
        await deliveryRepository.ScheduleRetryAsync(row.Id, attemptNumber, deliverAfter, cancellationToken);
        logger.LogWarning("Delivery {DeliveryId} failed. Scheduled retry {AttemptCount} at {DeliverAfter}. Error: {Error}",
            row.Id, attemptNumber, deliverAfter, result.Error);
    }

    internal static TimeSpan CalculateBackoff(int attemptCount)
    {
        var exponent = Math.Min(attemptCount - 1, 10);
        return RetryBaseDelay * Math.Pow(2, exponent);
    }
}
