using System.Diagnostics;
using System.Text.Json;
using Integrios.Application.Secrets;
using Integrios.Application.Telemetry;
using Integrios.Application.Transforms;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Integrios.Application.Ingestion;

public sealed record AcceptVerifiedWebhookCommand(
    Guid CallbackId,
    string? ContentType,
    IReadOnlyDictionary<string, string> Headers,
    ReadOnlyMemory<byte> RawBody)
    : IRequest<IngestEventResult>;

internal sealed class AcceptVerifiedWebhookCommandHandler(
    ISourceEndpointResolver endpointResolver,
    ISourceVerifierRegistry verifierRegistry,
    ISourceVerificationSecretResolver secretResolver,
    ITransformEvaluator evaluator,
    IEventAcceptance eventAcceptance,
    IntegriosMetrics metrics,
    ILogger<AcceptVerifiedWebhookCommandHandler> logger)
    : IRequestHandler<AcceptVerifiedWebhookCommand, IngestEventResult>
{
    public async Task<IngestEventResult> Handle(AcceptVerifiedWebhookCommand command, CancellationToken cancellationToken)
    {
        ResolvedSourceEndpoint endpoint = await endpointResolver.ResolveAsync(command.CallbackId, cancellationToken)
            ?? throw new SourceEndpointNotFoundException("No active webhook Source matches this callback URL.");

        await VerifyAsync(endpoint, command, cancellationToken);

        if (command.ContentType?.Split(';')[0].Trim().Equals("application/json", StringComparison.OrdinalIgnoreCase) != true)
            throw new WebhookPayloadException("The request Content-Type must be application/json.");

        JsonElement rawInput;
        try
        {
            rawInput = JsonSerializer.Deserialize<JsonElement>(command.RawBody.Span);
        }
        catch (JsonException)
        {
            throw new WebhookPayloadException("The request body must be valid JSON.");
        }
        if (rawInput.ValueKind != JsonValueKind.Object)
            throw new WebhookPayloadException("The request body must be a JSON object.");

        JsonElement context = BuildContext(command.Headers);
        SourceContractOutput output = SourceContractEvaluator.Evaluate(
            evaluator, endpoint.SourceContractSchema, endpoint.SourceMapping, rawInput, context);
        string? idempotencyKey = output.SourceEventId is { } sourceEventId
            ? $"{endpoint.SourceId}:{sourceEventId}"
            : null;

        var activity = Activity.Current;
        activity?.SetTag("tenant_id", endpoint.TenantId);
        activity?.SetTag("topic_id", endpoint.TopicId);
        activity?.SetTag("source_id", endpoint.SourceId);
        var accepted = await eventAcceptance.AcceptAsync(
            new EventSubmission
            {
                TenantId = endpoint.TenantId,
                TopicId = endpoint.TopicId,
                SourceId = endpoint.SourceId,
                SourceEventId = output.SourceEventId,
                EventType = output.EventType,
                Payload = output.Payload,
                Metadata = output.Metadata,
                IdempotencyKey = idempotencyKey,
            },
            activity?.Id,
            cancellationToken);
        activity?.SetTag("event_id", accepted.EventId);

        using var scope = logger.BeginScope(new Dictionary<string, object>
        {
            ["event_id"] = accepted.EventId, ["tenant_id"] = endpoint.TenantId,
            ["topic_id"] = endpoint.TopicId, ["source_id"] = endpoint.SourceId
        });
        if (!accepted.AlreadyAccepted)
        {
            metrics.RecordEventIngested();
            logger.LogInformation("Accepted webhook event {EventId} on topic {TopicId}.", accepted.EventId, endpoint.TopicId);
        }

        return new IngestEventResult
        {
            EventId = accepted.EventId,
            Status = accepted.Status,
            AcceptedAt = accepted.AcceptedAt,
            AlreadyAccepted = accepted.AlreadyAccepted
        };
    }

    private async Task VerifyAsync(
        ResolvedSourceEndpoint endpoint, AcceptVerifiedWebhookCommand command, CancellationToken cancellationToken)
    {
        // AllowUnverified connectors permit a Connection with no selected SourceVerification scheme.
        if (endpoint.SourceVerification is not { } verification)
            return;

        ISourceVerifier verifier = verifierRegistry.GetRequired(verification.Scheme);

        Dictionary<string, string> secrets = [];
        foreach (JsonProperty property in verification.SecretRefs.EnumerateObject())
        {
            string reference = property.Value.GetString()
                ?? throw new SourceVerificationException(
                    $"Source verification secret reference '{property.Name}' is invalid.");
            secrets[property.Name] = await secretResolver.ResolveAsync(
                new TenantSecretScope(endpoint.TenantId, endpoint.TenantSlug), reference, cancellationToken);
        }

        bool verified = verifier.Verify(command.RawBody, command.Headers, verification.Config, secrets);
        if (!verified)
            throw new SourceVerificationException("Signature verification failed.");
    }

    // Bounded, non-secret transport facts available to a webhook Source's mapping: lower-cased
    // request headers only. Method, path, and query are deliberately excluded.
    private static JsonElement BuildContext(IReadOnlyDictionary<string, string> headers)
    {
        var lowered = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach ((string name, string value) in headers)
            lowered[name.ToLowerInvariant()] = value;
        return JsonSerializer.SerializeToElement(new { headers = lowered });
    }
}
