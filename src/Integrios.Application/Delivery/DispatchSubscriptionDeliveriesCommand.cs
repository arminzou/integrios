using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Integrios.Application.Abstractions;
using Integrios.Application.Abstractions.Auth;
using Integrios.Application.Telemetry;
using Integrios.Domain.Delivery;
using Integrios.Domain.Integrations;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Integrios.Application.Delivery;

public sealed record DispatchSubscriptionDeliveriesCommand(int BatchSize) : IRequest<int>;

internal sealed class DispatchSubscriptionDeliveriesCommandHandler(
    ISubscriptionDeliveryQueue deliveryQueue,
    IDeliveryClient deliveryClient,
    ITransformEvaluator transformEvaluator,
    IAuthSchemeRegistry authSchemeRegistry,
    ISecretResolver secretResolver,
    DeliveryExecutionOptions executionOptions,
    IntegriosMetrics metrics,
    ILogger<DispatchSubscriptionDeliveriesCommandHandler> logger) : IRequestHandler<DispatchSubscriptionDeliveriesCommand, int>
{
    public async Task<int> Handle(DispatchSubscriptionDeliveriesCommand command, CancellationToken cancellationToken)
    {
        int processedCount = 0;

        while (processedCount < command.BatchSize && !cancellationToken.IsCancellationRequested)
        {
            SubscriptionDeliveryClaimResult? claim = await deliveryQueue.ClaimNextWithRecoveryAsync(cancellationToken);
            if (claim is null)
                break;

            if (claim is RecoveredSubscriptionDeliveryDeadLetter recovered)
            {
                metrics.RecordDeliveryDeadLettered(recovered.IntegrationKey);
                logger.LogWarning(
                    "Recovered expired attempt_id={AttemptId}, attempt_number={AttemptNumber} and dead-lettered delivery_id={DeliveryId}, subscription_id={SubscriptionId}, event_id={EventId}",
                    recovered.AttemptId,
                    recovered.AttemptNumber,
                    recovered.DeliveryId,
                    recovered.SubscriptionId,
                    recovered.EventId);
                continue;
            }

            if (claim is not ClaimedSubscriptionDelivery claimed)
                throw new InvalidOperationException($"Unknown delivery claim result '{claim.GetType().Name}'.");

            SubscriptionDeliveryWorkItem row = claimed.WorkItem;

            try
            {
                await DispatchAsync(row);
            }
            catch (Exception ex)
            {
                logger.LogError(
                    ex,
                    "Delivery attempt_id={AttemptId} for delivery_id={DeliveryId} was abandoned before finalization; lease recovery will reclaim it",
                    row.AttemptId,
                    row.Id);
            }

            processedCount++;
        }

        return processedCount;
    }

    private async Task DispatchAsync(SubscriptionDeliveryWorkItem row)
    {
        using var attemptDeadline = new CancellationTokenSource(executionOptions.AttemptDeadline);
        CancellationToken cancellationToken = attemptDeadline.Token;
        long startedTimestamp = Stopwatch.GetTimestamp();

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
        activity?.SetTag("attempt_id", row.AttemptId);
        activity?.SetTag("attempt_number", row.AttemptNumber);
        activity?.SetTag("integration_key", row.IntegrationKey);

        (string? payload, string? error) = ApplyTransform(row);
        if (error is not null)
        {
            var transformFailure = new DeliveryResult(
                false,
                0,
                error,
                FailurePhase: DeliveryFailurePhase.Transform);
            await FinalizeAsync(row, row.PayloadJson, transformFailure, Stopwatch.GetElapsedTime(startedTimestamp), cancellationToken);
            return;
        }

        DeliveryResult result;

        try
        {
            Action<HttpRequestMessage> decorate = await BuildRequestDecoratorAsync(row, cancellationToken);
            result = await deliveryClient.DeliverAsync(row.DestinationUrl, payload!, decorate, cancellationToken);
        }
        catch (DeliveryPreparationException ex)
        {
            result = new DeliveryResult(false, 0, ex.Message, FailurePhase: ex.FailurePhase);
        }
        catch (Exception ex)
        {
            result = new DeliveryResult(
                false,
                0,
                DeliveryConfigurationException.SafeMessage(ex),
                FailurePhase: DeliveryFailurePhase.RequestConstruction);
        }

        await FinalizeAsync(row, payload!, result, Stopwatch.GetElapsedTime(startedTimestamp), cancellationToken);
    }

    private async Task FinalizeAsync(
        SubscriptionDeliveryWorkItem row,
        string requestPayload,
        DeliveryResult result,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        DeliveryFailurePhase? failurePhase = result.Succeeded
            ? null
            : result.FailurePhase ?? DeliveryFailurePhase.Http;

        var completion = new DeliveryAttemptCompletion(
            row.Id,
            row.AttemptId,
            result.Succeeded,
            failurePhase,
            requestPayload,
            result.StatusCode == 0 ? null : result.StatusCode,
            null,
            result.Error);

        DeliveryFinalizationResult finalization = await deliveryQueue.FinalizeAsync(completion, cancellationToken);
        if (finalization.Status == DeliveryFinalizationStatus.OwnershipLost)
        {
            metrics.RecordDeliveryStaleFinalization();
            logger.LogWarning(
                "Discarded stale finalization for attempt_id={AttemptId}, delivery_id={DeliveryId}, subscription_id={SubscriptionId}, event_id={EventId}",
                row.AttemptId,
                row.Id,
                row.SubscriptionId,
                row.EventId);
            return;
        }

        metrics.RecordDeliveryAttemptDuration(duration.TotalSeconds, result.Succeeded ? "success" : "failed", row.IntegrationKey);
        RecordFailurePhaseMetric(failurePhase, row.IntegrationKey);

        switch (finalization.Disposition)
        {
            case SubscriptionDeliveryDisposition.Succeeded:
                metrics.RecordDeliverySucceeded(row.IntegrationKey);
                logger.LogInformation(
                    "Delivery succeeded for attempt_id={AttemptId}, delivery_id={DeliveryId}, subscription_id={SubscriptionId}, event_id={EventId}",
                    row.AttemptId,
                    row.Id,
                    row.SubscriptionId,
                    row.EventId);
                break;
            case SubscriptionDeliveryDisposition.RetryScheduled:
                metrics.RecordDeliveryFailed(row.IntegrationKey, HttpStatusClass(result));
                LogFailure(row, result, failurePhase, "scheduled for retry");
                break;
            case SubscriptionDeliveryDisposition.DeadLettered:
                metrics.RecordDeliveryDeadLettered(row.IntegrationKey);
                LogFailure(row, result, failurePhase, "dead-lettered");
                break;
            default:
                throw new InvalidOperationException("Applied finalization did not return a delivery disposition.");
        }
    }

    private async Task<Action<HttpRequestMessage>> BuildRequestDecoratorAsync(
        SubscriptionDeliveryWorkItem row,
        CancellationToken cancellationToken)
    {
        IAuthSchemeHandler? handler = null;
        ConnectionAuth? destinationAuth = null;
        Dictionary<string, string> secrets = [];
        string? resolvingReference = null;

        if (!string.IsNullOrWhiteSpace(row.DestinationAuthJson))
        {
            try
            {
                destinationAuth = JsonSerializer.Deserialize<ConnectionAuth>(row.DestinationAuthJson)
                    ?? throw new DeliveryConfigurationException("Destination auth snapshot is invalid.");
                handler = authSchemeRegistry.GetRequired(destinationAuth.Scheme);
            }
            catch (Exception ex)
            {
                throw new DeliveryPreparationException(
                    DeliveryFailurePhase.RequestConstruction,
                    DeliveryConfigurationException.SafeMessage(ex, "Destination auth snapshot could not be read."),
                    ex);
            }

            try
            {
                foreach (JsonProperty property in destinationAuth.SecretRefs.EnumerateObject())
                {
                    string reference = property.Value.GetString()
                        ?? throw new InvalidOperationException($"Secret reference '{property.Name}' is invalid.");
                    resolvingReference = reference;
                    secrets[property.Name] = await secretResolver.ResolveAsync(
                        new TenantSecretScope(row.TenantId, row.TenantSlug),
                        reference,
                        cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                string safeReference = SecretReferenceName.IsValid(resolvingReference)
                    ? resolvingReference!
                    : "invalid";
                throw new DeliveryPreparationException(
                    DeliveryFailurePhase.SecretResolution,
                    $"Secret reference '{safeReference}' could not be resolved using provider '{secretResolver.ProviderName}'.");
            }
        }

        return request =>
        {
            if (handler is not null && destinationAuth is not null)
                handler.Apply(request, destinationAuth.Config, secrets);

            SetReservedHeader(request, "Integrios-Event-Id", row.EventId.ToString());
            SetReservedHeader(request, "Integrios-Delivery-Id", row.Id.ToString());
            SetReservedHeader(request, "Integrios-Attempt-Id", row.AttemptId.ToString());
            SetReservedHeader(request, "Integrios-Attempt-Number", row.AttemptNumber.ToString(CultureInfo.InvariantCulture));
        };
    }

    private static void SetReservedHeader(HttpRequestMessage request, string name, string value)
    {
        request.Headers.Remove(name);
        request.Headers.TryAddWithoutValidation(name, value);
    }

    private void LogFailure(
        SubscriptionDeliveryWorkItem row,
        DeliveryResult result,
        DeliveryFailurePhase? failurePhase,
        string disposition)
    {
        logger.LogWarning(
            "Delivery attempt_id={AttemptId} for delivery_id={DeliveryId}, subscription_id={SubscriptionId}, event_id={EventId} failed in failure_phase={FailurePhase} and was {Disposition}: {Error}",
            row.AttemptId,
            row.Id,
            row.SubscriptionId,
            row.EventId,
            MapFailurePhase(failurePhase),
            disposition,
            result.Error ?? $"HTTP {result.StatusCode}");
    }

    private void RecordFailurePhaseMetric(DeliveryFailurePhase? failurePhase, string integrationKey)
    {
        if (failurePhase == DeliveryFailurePhase.SecretResolution)
            metrics.RecordDeliverySecretResolutionFailure(integrationKey);
        else if (failurePhase == DeliveryFailurePhase.RequestConstruction)
            metrics.RecordDeliveryRequestConstructionFailure(integrationKey);
    }

    private static string MapFailurePhase(DeliveryFailurePhase? failurePhase) => failurePhase switch
    {
        DeliveryFailurePhase.Transform => "transform",
        DeliveryFailurePhase.SecretResolution => "secret_resolution",
        DeliveryFailurePhase.RequestConstruction => "request_construction",
        DeliveryFailurePhase.Http => "http",
        _ => "none"
    };

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
            string output = transformEvaluator.Evaluate(
                engine,
                version,
                expression,
                row.PayloadJson,
                context);
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

    private sealed class DeliveryPreparationException(
        DeliveryFailurePhase failurePhase,
        string message,
        Exception? innerException = null)
        : Exception(message, innerException)
    {
        public DeliveryFailurePhase FailurePhase { get; } = failurePhase;
    }
}
