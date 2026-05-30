using System.Text.Json;
using Integrios.Application.Abstractions;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Integrios.Application.Delivery;

public sealed record DispatchSubscriptionDeliveriesCommand(int BatchSize, int MaxAttempts) : IRequest<int>
{
    // Delivery retry/DLQ policy: an attempt count beyond this dead-letters the delivery.
    public const int DefaultMaxAttempts = 3;
}

internal sealed class DispatchSubscriptionDeliveriesCommandHandler(
    ISubscriptionDeliveryQueue deliveryQueue,
    IDeliveryAttemptRepository attemptRepository,
    IDeliveryClient deliveryClient,
    ITransformEvaluator transformEvaluator,
    ILogger<DispatchSubscriptionDeliveriesCommandHandler> logger) : IRequestHandler<DispatchSubscriptionDeliveriesCommand, int>
{
    private static readonly TimeSpan RetryBaseDelay = TimeSpan.FromSeconds(30);

    public async Task<int> Handle(DispatchSubscriptionDeliveriesCommand command, CancellationToken cancellationToken)
    {
        var rows = await deliveryQueue.ClaimBatchAsync(command.BatchSize, cancellationToken);

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
            await deliveryQueue.MarkDeadLetteredAsync(row.Id, cancellationToken);
            return;
        }

        var attemptNumber = row.AttemptCount + 1;
        var startedAt = DateTimeOffset.UtcNow;

        logger.LogInformation("Dispatching delivery {DeliveryId} (event {EventId} → subscription {SubscriptionId}, attempt {N}) to {Url}",
            row.Id, row.EventId, row.SubscriptionId, attemptNumber, row.DestinationUrl);

        var (payloadJson, transformError) = ApplyTransform(row);

        DeliveryResult result;
        if (transformError is not null)
        {
            logger.LogError("Transform failed for delivery {DeliveryId}: {Error}", row.Id, transformError);
            result = new DeliveryResult(false, 0, transformError);
        }
        else
        {
            result = await deliveryClient.DeliverAsync(row.DestinationUrl, payloadJson!, cancellationToken);
        }

        var completedAt = DateTimeOffset.UtcNow;

        await attemptRepository.RecordAsync(
            eventId: row.EventId,
            subscriptionId: row.SubscriptionId,
            destinationConnectionId: row.DestinationConnectionId,
            attemptNumber: attemptNumber,
            status: result.Succeeded ? "succeeded" : "failed",
            requestPayloadJson: payloadJson ?? row.PayloadJson,
            responseStatusCode: result.StatusCode > 0 ? result.StatusCode : null,
            responseBody: null,
            errorMessage: result.Error,
            startedAt: startedAt,
            completedAt: completedAt,
            cancellationToken: cancellationToken);

        if (result.Succeeded)
        {
            await deliveryQueue.MarkSucceededAsync(row.Id, cancellationToken);
            logger.LogInformation("Delivery {DeliveryId} succeeded — HTTP {StatusCode}", row.Id, result.StatusCode);
            return;
        }

        if (attemptNumber >= maxAttempts)
        {
            await deliveryQueue.MarkDeadLetteredAsync(row.Id, cancellationToken);
            logger.LogError("Delivery {DeliveryId} dead-lettered after {AttemptCount} attempt(s). Last error: {Error}",
                row.Id, attemptNumber, result.Error);
            return;
        }

        var deliverAfter = DateTimeOffset.UtcNow + CalculateBackoff(attemptNumber);
        await deliveryQueue.ScheduleRetryAsync(row.Id, attemptNumber, deliverAfter, cancellationToken);
        logger.LogWarning("Delivery {DeliveryId} failed. Scheduled retry {AttemptCount} at {DeliverAfter}. Error: {Error}",
            row.Id, attemptNumber, deliverAfter, result.Error);
    }

    private (string? payload, string? error) ApplyTransform(SubscriptionDeliveryWorkItem row)
    {
        if (string.IsNullOrWhiteSpace(row.TransformConfigSnapshot))
            return (row.PayloadJson, null);

        try
        {
            using var doc = JsonDocument.Parse(row.TransformConfigSnapshot);
            var root = doc.RootElement;

            if (!root.TryGetProperty("engine", out var engineEl) ||
                !root.TryGetProperty("version", out var versionEl) ||
                !root.TryGetProperty("expression", out var expressionEl))
                return (null, "Transform config is missing required fields (engine, version, expression).");

            var engine = engineEl.GetString() ?? string.Empty;
            var version = versionEl.GetString() ?? string.Empty;
            var expression = expressionEl.GetString() ?? string.Empty;

            var context = new TransformContext(row.EventType, row.TopicName, row.AcceptedAt);
            var output = transformEvaluator.Evaluate(expression, row.PayloadJson, context);
            return (output, null);
        }
        catch (TransformEvaluationException ex)
        {
            return (null, ex.Message);
        }
        catch (Exception ex)
        {
            return (null, $"Unexpected transform error: {ex.Message}");
        }
    }

    internal static TimeSpan CalculateBackoff(int attemptCount)
    {
        var exponent = Math.Min(attemptCount - 1, 10);
        return RetryBaseDelay * Math.Pow(2, exponent);
    }
}
