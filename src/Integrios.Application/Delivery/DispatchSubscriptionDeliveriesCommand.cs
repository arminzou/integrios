using System.Diagnostics;
using System.Text.Json;
using Integrios.Application.Abstractions;
using Integrios.Application.Abstractions.Auth;
using Integrios.Application.Telemetry;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Integrios.Application.Delivery;

public sealed record DispatchSubscriptionDeliveriesCommand(int BatchSize) : IRequest<int>;

internal sealed class DispatchSubscriptionDeliveriesCommandHandler(
    ISubscriptionDeliveryQueue deliveryQueue,
    IDeliveryAttemptRepository attemptRepository,
    IDeliveryClient deliveryClient,
    ITransformEvaluator transformEvaluator,
    IAuthSchemeRegistry authSchemeRegistry,
    ISecretResolver secretResolver,
    RetryPolicy retryPolicy,
    IntegriosMetrics metrics,
    ILogger<DispatchSubscriptionDeliveriesCommandHandler> logger) : IRequestHandler<DispatchSubscriptionDeliveriesCommand, int>
{
    public async Task<int> Handle(DispatchSubscriptionDeliveriesCommand command, CancellationToken cancellationToken)
    {
        IReadOnlyList<SubscriptionDeliveryWorkItem> rows = await deliveryQueue.ClaimBatchAsync(command.BatchSize, cancellationToken);

        foreach (SubscriptionDeliveryWorkItem row in rows)
        {
            await DispatchAsync(row, cancellationToken);
        }

        return rows.Count;
    }

    private async Task DispatchAsync(SubscriptionDeliveryWorkItem row, CancellationToken cancellationToken)
    {
        using Activity? activity = ActivitySources.StartLinkedSpan("subscription.deliver", row.Traceparent);
        using IDisposable? scope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["event_id"] = row.EventId,
            ["subscription_id"] = row.SubscriptionId,
            ["delivery_id"] = row.Id
        });
        activity?.SetTag("event_id", row.EventId);
        activity?.SetTag("subscription_id", row.SubscriptionId);
        activity?.SetTag("delivery_id", row.Id);
        activity?.SetTag("integration_key", row.IntegrationKey);

        int attemptNumber = row.AttemptCount + 1;
        (string? payload, string? error) = ApplyTransform(row);
        if (error is not null)
        {
            DateTimeOffset transformStartedAt = DateTimeOffset.UtcNow;
            DateTimeOffset transformCompletedAt = transformStartedAt;
            DeliveryResult transformFailure = new(false, 0, error);

            await attemptRepository.RecordAsync(
                row.EventId,
                row.SubscriptionId,
                row.DestinationConnectionId,
                attemptNumber,
                "failed",
                row.PayloadJson,
                null,
                null,
                transformFailure.Error,
                transformStartedAt,
                transformCompletedAt,
                cancellationToken);

            await HandleFailureAsync(row, transformFailure, cancellationToken, attemptNumber, 0);
            return;
        }

        DeliveryResult result;
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;

        try
        {
            Action<HttpRequestMessage>? decorate = await BuildRequestDecoratorAsync(row, cancellationToken);
            result = await deliveryClient.DeliverAsync(row.DestinationUrl, payload!, decorate, cancellationToken);
        }
        catch (Exception ex)
        {
            result = new DeliveryResult(false, 0, ex.Message);
        }

        DateTimeOffset completedAt = DateTimeOffset.UtcNow;
        double durationSeconds = (completedAt - startedAt).TotalSeconds;

        await attemptRepository.RecordAsync(
            row.EventId,
            row.SubscriptionId,
            row.DestinationConnectionId,
            attemptNumber,
            result.Succeeded ? "succeeded" : "failed",
            payload!,
            result.StatusCode == 0 ? null : result.StatusCode,
            null,
            result.Error,
            startedAt,
            completedAt,
            cancellationToken);

        if (result.Succeeded)
        {
            await deliveryQueue.MarkSucceededAsync(row.Id, cancellationToken);
            metrics.RecordDeliverySucceeded(row.IntegrationKey);
            metrics.RecordDeliveryAttemptDuration(durationSeconds, "success", row.IntegrationKey);
            logger.LogInformation(
                "Delivery succeeded for delivery_id={DeliveryId}, subscription_id={SubscriptionId}, event_id={EventId}",
                row.Id,
                row.SubscriptionId,
                row.EventId);
            return;
        }

        await HandleFailureAsync(row, result, cancellationToken, attemptNumber, durationSeconds);
    }

    private async Task<Action<HttpRequestMessage>?> BuildRequestDecoratorAsync(
        SubscriptionDeliveryWorkItem row,
        CancellationToken cancellationToken)
    {
        if (row.DestinationAuth is null)
        {
            return null;
        }

        IAuthSchemeHandler handler = authSchemeRegistry.GetRequired(row.DestinationAuth.Scheme);
        Dictionary<string, string> secrets = [];

        foreach (JsonProperty property in row.DestinationAuth.SecretRefs.EnumerateObject())
        {
            string reference = property.Value.GetString()
                ?? throw new InvalidOperationException($"Secret reference '{property.Name}' is invalid.");
            secrets[property.Name] = await secretResolver.ResolveAsync(row.TenantId, reference, cancellationToken);
        }

        return request => handler.Apply(request, row.DestinationAuth.Config, secrets);
    }

    private async Task HandleFailureAsync(
        SubscriptionDeliveryWorkItem row,
        DeliveryResult result,
        CancellationToken cancellationToken,
        int? attemptNumber = null,
        double? durationSeconds = null)
    {
        int nextAttempt = attemptNumber ?? row.AttemptCount + 1;
        if (nextAttempt >= RetryPolicy.DefaultMaxAttempts)
        {
            await deliveryQueue.MarkDeadLetteredAsync(row.Id, cancellationToken);
            metrics.RecordDeliveryDeadLettered(row.IntegrationKey);
        }
        else
        {
            DateTimeOffset deliverAfter = DateTimeOffset.UtcNow + retryPolicy.CalculateBackoff(nextAttempt);
            await deliveryQueue.ScheduleRetryAsync(row.Id, nextAttempt, deliverAfter, cancellationToken);
            metrics.RecordDeliveryFailed(row.IntegrationKey, HttpStatusClass(result));
        }

        metrics.RecordDeliveryAttemptDuration(durationSeconds ?? 0, "failed", row.IntegrationKey);
        logger.LogWarning(
            "Delivery failed for delivery_id={DeliveryId}, subscription_id={SubscriptionId}, event_id={EventId}: {Error}",
            row.Id,
            row.SubscriptionId,
            row.EventId,
            result.Error ?? $"HTTP {result.StatusCode}");
    }

    private static string HttpStatusClass(DeliveryResult result) =>
        result.IsTimeout ? "timeout" : result.StatusCode switch
        {
            >= 200 and < 300 => "2xx",
            >= 400 and < 500 => "4xx",
            >= 500 => "5xx",
            _ => "error"
        };

    private (string? payload, string? error) ApplyTransform(SubscriptionDeliveryWorkItem row)
    {
        if (string.IsNullOrWhiteSpace(row.TransformConfigSnapshot))
        {
            return (row.PayloadJson, null);
        }

        try
        {
            using var doc = JsonDocument.Parse(row.TransformConfigSnapshot);
            JsonElement root = doc.RootElement;
            if (!root.TryGetProperty("engine", out JsonElement engineEl)
                || !root.TryGetProperty("version", out JsonElement versionEl)
                || !root.TryGetProperty("expression", out JsonElement expressionEl))
            {
                return (null, "Transform config is missing required fields (engine, version, expression).");
            }

            string engine = engineEl.GetString() ?? string.Empty;
            string version = versionEl.GetString() ?? string.Empty;
            string expression = expressionEl.GetString() ?? string.Empty;
            TransformContext context = new(row.EventType, row.TopicName, row.AcceptedAt);
            string output = transformEvaluator.Evaluate(expression, row.PayloadJson, context);
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
