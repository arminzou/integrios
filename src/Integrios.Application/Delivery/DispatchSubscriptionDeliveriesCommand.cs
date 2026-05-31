using System.Diagnostics;
using System.Text.Json;
using Integrios.Application.Abstractions;
using Integrios.Application.Telemetry;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Integrios.Application.Delivery;

public sealed record DispatchSubscriptionDeliveriesCommand(int BatchSize, int MaxAttempts) : IRequest<int>;

internal sealed class DispatchSubscriptionDeliveriesCommandHandler(
    ISubscriptionDeliveryQueue deliveryQueue,
    IDeliveryAttemptRepository attemptRepository,
    IDeliveryClient deliveryClient,
    ITransformEvaluator transformEvaluator,
    RetryPolicy retryPolicy,
    IntegriosMetrics metrics,
    ILogger<DispatchSubscriptionDeliveriesCommandHandler> logger) : IRequestHandler<DispatchSubscriptionDeliveriesCommand, int>
{
    public async Task<int> Handle(DispatchSubscriptionDeliveriesCommand command, CancellationToken cancellationToken)
    {
        var rows = await deliveryQueue.ClaimBatchAsync(command.BatchSize, cancellationToken);

        foreach (var row in rows)
            await DispatchAsync(row, command.MaxAttempts, cancellationToken);

        return rows.Count;
    }

    private async Task DispatchAsync(SubscriptionDeliveryWorkItem row, int maxAttempts, CancellationToken cancellationToken)
    {
        // Re-parent under the originating event's trace so retries on later ticks stay continuous.
        using var activity = ActivitySources.StartLinkedSpan("subscription.deliver", row.Traceparent);
        activity?.SetTag("event_id", row.EventId);
        activity?.SetTag("subscription_id", row.SubscriptionId);
        activity?.SetTag("delivery_id", row.Id);
        activity?.SetTag("integration_key", row.IntegrationKey);

        using var scope = logger.BeginScope(new Dictionary<string, object>
        {
            ["event_id"] = row.EventId,
            ["delivery_id"] = row.Id,
            ["subscription_id"] = row.SubscriptionId
        });

        if (string.IsNullOrWhiteSpace(row.DestinationUrl))
        {
            logger.LogWarning("Subscription {SubscriptionId} has no destination URL. Dead-lettering delivery {DeliveryId}.",
                row.SubscriptionId, row.Id);
            await deliveryQueue.MarkDeadLetteredAsync(row.Id, cancellationToken);
            metrics.RecordDeliveryDeadLettered(row.IntegrationKey);
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
        var durationSeconds = (completedAt - startedAt).TotalSeconds;
        activity?.SetTag("http_status_class", HttpStatusClass(result));

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
            metrics.RecordDeliverySucceeded(row.IntegrationKey);
            metrics.RecordDeliveryAttemptDuration(durationSeconds, "success", row.IntegrationKey);
            logger.LogInformation("Delivery {DeliveryId} succeeded — HTTP {StatusCode}", row.Id, result.StatusCode);
            return;
        }

        metrics.RecordDeliveryAttemptDuration(durationSeconds, "failure", row.IntegrationKey);

        if (attemptNumber >= maxAttempts)
        {
            await deliveryQueue.MarkDeadLetteredAsync(row.Id, cancellationToken);
            metrics.RecordDeliveryDeadLettered(row.IntegrationKey);
            logger.LogError("Delivery {DeliveryId} dead-lettered after {AttemptCount} attempt(s). Last error: {Error}",
                row.Id, attemptNumber, result.Error);
            return;
        }

        metrics.RecordDeliveryFailed(row.IntegrationKey, HttpStatusClass(result));

        var deliverAfter = DateTimeOffset.UtcNow + retryPolicy.CalculateBackoff(attemptNumber);
        await deliveryQueue.ScheduleRetryAsync(row.Id, attemptNumber, deliverAfter, cancellationToken);
        logger.LogWarning("Delivery {DeliveryId} failed. Scheduled retry {AttemptCount} at {DeliverAfter}. Error: {Error}",
            row.Id, attemptNumber, deliverAfter, result.Error);
    }

    private static string HttpStatusClass(DeliveryResult result)
    {
        if (result.IsTimeout)
            return "timeout";

        return result.StatusCode switch
        {
            >= 200 and < 300 => "2xx",
            >= 400 and < 500 => "4xx",
            >= 500 and < 600 => "5xx",
            _ => "error"
        };
    }

    private (string? payload, string? error) ApplyTransform(SubscriptionDeliveryWorkItem row)
    {
        using var activity = ActivitySources.Application.StartActivity("subscription.transform");

        if (string.IsNullOrWhiteSpace(row.TransformConfigSnapshot))
        {
            activity?.SetTag("transform", "noop");
            return (row.PayloadJson, null);
        }

        activity?.SetTag("transform", "evaluated");

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
}
