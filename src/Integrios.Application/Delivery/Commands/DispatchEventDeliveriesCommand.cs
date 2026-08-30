using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Integrios.Application.Delivery;
using Integrios.Application.Secrets;
using Integrios.Application.Authoring.Subscriptions;
using Integrios.Application.Telemetry;
using Integrios.Application.Transforms;
using Integrios.Domain.Entities;
using Integrios.Domain.Enums;
using Integrios.Domain.ValueObjects;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Integrios.Application.Delivery;

public sealed record DispatchEventDeliveriesCommand(int BatchSize) : IRequest<int>;

internal sealed class DispatchEventDeliveriesCommandHandler(
    IEventDeliveryQueue deliveryQueue,
    IDeliveryClient deliveryClient,
    ITransformEvaluator transformEvaluator,
    IDestinationAuthenticatorRegistry authSchemeRegistry,
    IDestinationAuthenticationSecretResolver secretResolver,
    DeliveryExecutionOptions executionOptions,
    IntegriosMetrics metrics,
    ILogger<DispatchEventDeliveriesCommandHandler> logger) : IRequestHandler<DispatchEventDeliveriesCommand, int>
{
    public async Task<int> Handle(DispatchEventDeliveriesCommand command, CancellationToken cancellationToken)
    {
        int processedCount = 0;

        while (processedCount < command.BatchSize && !cancellationToken.IsCancellationRequested)
        {
            EventDeliveryClaimResult? claim = await deliveryQueue.ClaimNextWithRecoveryAsync(cancellationToken);
            if (claim is null)
                break;

            if (claim is RecoveredEventDeliveryDeadLetter recovered)
            {
                metrics.RecordDeliveryDeadLettered(recovered.ConnectorKey);
                logger.LogWarning(
                    "Recovered expired attempt_id={AttemptId}, attempt_number={AttemptNumber} and dead-lettered delivery_id={DeliveryId}, subscription_id={SubscriptionId}, event_id={EventId}",
                    recovered.AttemptId,
                    recovered.AttemptNumber,
                    recovered.DeliveryId,
                    recovered.SubscriptionId,
                    recovered.EventId);
                continue;
            }

            if (claim is not ClaimedEventDelivery claimed)
                throw new InvalidOperationException($"Unknown delivery claim result '{claim.GetType().Name}'.");

            EventDeliveryWorkItem row = claimed.WorkItem;

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

    private async Task DispatchAsync(EventDeliveryWorkItem row)
    {
        using var attemptDeadline = new CancellationTokenSource(executionOptions.AttemptDeadline);
        CancellationToken cancellationToken = attemptDeadline.Token;
        long startedTimestamp = Stopwatch.GetTimestamp();

        using Activity? attemptActivity = ActivitySources.StartLinkedSpan("delivery.attempt", row.Traceparent);
        using IDisposable? scope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["event_id"] = row.EventId,
            ["subscription_id"] = row.SubscriptionId,
            ["delivery_id"] = row.Id
        });
        attemptActivity?.SetTag("integrios.event.id", row.EventId);
        attemptActivity?.SetTag("integrios.subscription.id", row.SubscriptionId);
        attemptActivity?.SetTag("integrios.delivery.id", row.Id);
        attemptActivity?.SetTag("integrios.attempt.id", row.AttemptId);
        attemptActivity?.SetTag("integrios.attempt.number", row.AttemptNumber);
        attemptActivity?.SetTag("integrios.connector.key", row.ConnectorKey);

        HttpExecutionSnapshot snapshot;
        try
        {
            snapshot = ReadSnapshot(row);
        }
        catch (DeliveryPreparationException ex)
        {
            var snapshotFailure = new DeliveryResult(false, 0, ex.Message, FailurePhase: ex.FailurePhase);
            await FinalizeAsync(row, null, snapshotFailure, Stopwatch.GetElapsedTime(startedTimestamp), cancellationToken, attemptActivity);
            return;
        }

        string? payload = null;
        if (snapshot.Request.Body == "json")
        {
            using Activity? transformActivity = ActivitySources.Application.StartActivity("delivery.transform");
            (payload, string? error) = ApplyTransform(row);
            if (error is not null)
            {
                transformActivity?.SetStatus(ActivityStatusCode.Error);
                var transformFailure = new DeliveryResult(
                    false,
                    0,
                    error,
                    FailurePhase: DeliveryFailurePhase.Transform);
                await FinalizeAsync(row, row.PayloadJson, transformFailure, Stopwatch.GetElapsedTime(startedTimestamp), cancellationToken, attemptActivity);
                return;
            }
        }

        DeliveryResult result;
        OutboundHttpMessage? outboundRequest = null;

        try
        {
            outboundRequest = await BuildOutboundRequestAsync(row, snapshot, payload, cancellationToken);
            using Activity? httpActivity = ActivitySources.Application.StartActivity("delivery.http");
            result = await deliveryClient.DeliverAsync(outboundRequest, snapshot.HttpSuccess, cancellationToken);
            if (!result.Succeeded)
                httpActivity?.SetStatus(ActivityStatusCode.Error);
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

        await FinalizeAsync(row, outboundRequest?.JsonBody, result, Stopwatch.GetElapsedTime(startedTimestamp), cancellationToken, attemptActivity);
    }

    private async Task FinalizeAsync(
        EventDeliveryWorkItem row,
        string? requestPayload,
        DeliveryResult result,
        TimeSpan duration,
        CancellationToken cancellationToken,
        Activity? attemptActivity)
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
            result.Error,
            IsTerminalFailure: DeliveryFailureClassifier.IsTerminal(result),
            RetryAfter: result.RetryAfter);

        if (failurePhase is not null)
            attemptActivity?.SetTag("integrios.failure_phase", MapFailurePhase(failurePhase));

        using Activity? finalizeActivity = ActivitySources.Application.StartActivity("delivery.finalize");
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

        metrics.RecordDeliveryAttemptDuration(duration.TotalSeconds, result.Succeeded ? "success" : "failed", row.ConnectorKey);
        RecordFailurePhaseMetric(failurePhase, row.ConnectorKey);

        switch (finalization.Disposition)
        {
            case EventDeliveryDisposition.Succeeded:
                metrics.RecordDeliverySucceeded(row.ConnectorKey);
                logger.LogInformation(
                    "Delivery succeeded for attempt_id={AttemptId}, delivery_id={DeliveryId}, subscription_id={SubscriptionId}, event_id={EventId}",
                    row.AttemptId,
                    row.Id,
                    row.SubscriptionId,
                    row.EventId);
                break;
            case EventDeliveryDisposition.RetryScheduled:
                metrics.RecordDeliveryFailed(row.ConnectorKey, HttpStatusClass(result));
                LogFailure(row, result, failurePhase, "scheduled for retry");
                break;
            case EventDeliveryDisposition.DeadLettered:
                metrics.RecordDeliveryDeadLettered(row.ConnectorKey);
                LogFailure(row, result, failurePhase, "dead-lettered");
                break;
            default:
                throw new InvalidOperationException("Applied finalization did not return a delivery disposition.");
        }
    }

    // The snapshot was validated by Admin authoring and written by fanout from those validated
    // rows, so dispatch reads it rather than re-validating: re-running the authoring rules here
    // would make a later rule change start failing deliveries that are already in flight, which is
    // the exact drift the snapshot exists to prevent. Only the format version is checked, because a
    // future version may mean something this build cannot execute.
    private static HttpExecutionSnapshot ReadSnapshot(EventDeliveryWorkItem row)
    {
        try
        {
            HttpExecutionSnapshot snapshot = JsonSerializer.Deserialize<HttpExecutionSnapshot>(
                row.HttpExecutionSnapshotJson, StoredJson.Options)
                ?? throw new DeliveryConfigurationException("HTTP execution snapshot is invalid.");
            if (snapshot.Version != HttpExecutionSnapshot.CurrentVersion)
                throw new DeliveryConfigurationException($"Unsupported HTTP execution snapshot version '{snapshot.Version}'.");

            return snapshot;
        }
        catch (Exception ex)
        {
            throw new DeliveryPreparationException(
                DeliveryFailurePhase.RequestConstruction,
                DeliveryConfigurationException.SafeMessage(ex, "HTTP execution snapshot could not be read."),
                ex);
        }
    }

    private async Task<OutboundHttpMessage> BuildOutboundRequestAsync(
        EventDeliveryWorkItem row,
        HttpExecutionSnapshot snapshot,
        string? transformedPayload,
        CancellationToken cancellationToken)
    {
        string requestUri;
        try
        {
            requestUri = HttpTargetComposer.Compose(snapshot.BaseUri, snapshot.Request.Path);
        }
        catch (Exception ex)
        {
            throw new DeliveryPreparationException(
                DeliveryFailurePhase.RequestConstruction,
                DeliveryConfigurationException.SafeMessage(ex, "HTTP request target could not be constructed."),
                ex);
        }

        Dictionary<string, string> headers = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string name, string value) in snapshot.Request.Headers)
        {
            if (!headers.TryAdd(name, value))
                throw new DeliveryConfigurationException($"Outbound header '{name}' is configured more than once.");
        }

        IDestinationAuthenticator? handler = null;
        DestinationAuthentication? destinationAuth = snapshot.DestinationAuthentication;
        Dictionary<string, string> secrets = [];
        string? resolvingReference = null;

        if (destinationAuth is not null)
        {
            try
            {
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

        if (handler is not null && destinationAuth is not null)
            handler.Apply(headers, destinationAuth.Config, secrets);

        // Assigned rather than added: Integrios delivery identity is authoritative even over a
        // legacy snapshot written before authoring rejected these names.
        headers["Integrios-Event-Id"] = row.EventId.ToString();
        headers["Integrios-Delivery-Id"] = row.Id.ToString();
        headers["Integrios-Attempt-Id"] = row.AttemptId.ToString();
        headers["Integrios-Attempt-Number"] = row.AttemptNumber.ToString(CultureInfo.InvariantCulture);

        string? jsonBody = snapshot.Request.Body == "json" ? transformedPayload : null;
        return new OutboundHttpMessage(snapshot.Request.Method, requestUri, headers, jsonBody);
    }

    private void LogFailure(
        EventDeliveryWorkItem row,
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

    private void RecordFailurePhaseMetric(DeliveryFailurePhase? failurePhase, string connectorKey)
    {
        if (failurePhase == DeliveryFailurePhase.SecretResolution)
            metrics.RecordDeliverySecretResolutionFailure(connectorKey);
        else if (failurePhase == DeliveryFailurePhase.RequestConstruction)
            metrics.RecordDeliveryRequestConstructionFailure(connectorKey);
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

    private (string? payload, string? error) ApplyTransform(EventDeliveryWorkItem row)
    {
        if (string.IsNullOrWhiteSpace(row.MappingConfigSnapshot))
        {
            return (row.PayloadJson, null);
        }

        try
        {
            using var doc = JsonDocument.Parse(row.MappingConfigSnapshot);
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
            var transform = new TransformSpec(engine, version, expression);
            TransformContext context = new(row.EventType, row.TopicName, row.AcceptedAt);
            string output = transformEvaluator.Evaluate(
                transform,
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
