using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Integrios.Application.Secrets;
using Integrios.Application.Telemetry;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Integrios.Application.Ingestion;

public sealed record AcceptVerifiedWebhookCommand(
    string ConnectorKey,
    Guid EndpointId,
    string? ContentType,
    IReadOnlyDictionary<string, string> Headers,
    ReadOnlyMemory<byte> RawBody)
    : IRequest<IngestEventResult>;

internal sealed class AcceptVerifiedWebhookCommandHandler(
    ISourceEndpointResolver endpointResolver,
    ISourceVerificationSecretResolver secretResolver,
    IEventAcceptance eventAcceptance,
    IntegriosMetrics metrics,
    ILogger<AcceptVerifiedWebhookCommandHandler> logger)
    : IRequestHandler<AcceptVerifiedWebhookCommand, IngestEventResult>
{
    public async Task<IngestEventResult> Handle(AcceptVerifiedWebhookCommand command, CancellationToken cancellationToken)
    {
        ResolvedSourceEndpoint endpoint = await endpointResolver.ResolveAsync(command.ConnectorKey, command.EndpointId, cancellationToken)
            ?? throw new SourceEndpointNotFoundException("No active webhook Source matches this callback URL.");

        EventSubmission submission = await CreateSubmissionAsync(endpoint, command, cancellationToken);
        var activity = Activity.Current;
        activity?.SetTag("tenant_id", endpoint.TenantId);
        activity?.SetTag("topic_id", endpoint.TopicId);
        activity?.SetTag("source_id", endpoint.SourceId);
        var accepted = await eventAcceptance.AcceptAsync(submission, activity?.Id, cancellationToken);
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

        return new IngestEventResult { EventId = accepted.EventId, Status = accepted.Status, AcceptedAt = accepted.AcceptedAt, AlreadyAccepted = accepted.AlreadyAccepted };
    }

    private async Task<EventSubmission> CreateSubmissionAsync(ResolvedSourceEndpoint source, AcceptVerifiedWebhookCommand command, CancellationToken cancellationToken)
    {
        if (command.ContentType?.Split(';')[0].Trim().Equals("application/json", StringComparison.OrdinalIgnoreCase) != true)
            throw new WebhookPayloadException("The request Content-Type must be application/json.");

        string signature = RequiredHeader(command.Headers, "X-Hub-Signature-256")
            ?? throw new SourceVerificationException("Missing required signature header 'X-Hub-Signature-256'.");
        if (!signature.StartsWith("sha256=", StringComparison.Ordinal))
            throw new SourceVerificationException("Signature header does not carry the expected prefix.");

        byte[] provided;
        try { provided = Convert.FromHexString(signature[7..]); }
        catch (FormatException) { throw new SourceVerificationException("Signature header is not validly encoded."); }

        string secretReference = source.SourceVerification.SecretRefs.GetProperty("secret").GetString()
            ?? throw new SourceVerificationException("The source Connection has no configured verification secret.");
        string secret = await secretResolver.ResolveAsync(new TenantSecretScope(source.TenantId, source.TenantSlug), secretReference, cancellationToken);
        byte[] expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), command.RawBody.Span);
        if (!CryptographicOperations.FixedTimeEquals(provided, expected))
            throw new SourceVerificationException("Signature verification failed.");

        JsonElement payload;
        try { payload = JsonSerializer.Deserialize<JsonElement>(command.RawBody.Span); }
        catch (JsonException) { throw new WebhookPayloadException("The request body must be valid JSON."); }
        if (payload.ValueKind != JsonValueKind.Object)
            throw new WebhookPayloadException("The request body must be a JSON object.");

        string deliveryId = RequiredHeader(command.Headers, "X-GitHub-Delivery")
            ?? throw new WebhookPayloadException("Missing required delivery identity header 'X-GitHub-Delivery'.");
        string eventType = RequiredHeader(command.Headers, "X-GitHub-Event")
            ?? throw new WebhookPayloadException("Missing required event-type header 'X-GitHub-Event'.");
        if (!ValidSegment(eventType))
            throw new WebhookPayloadException("Webhook event type is invalid.");
        if (payload.TryGetProperty("action", out JsonElement action) && action.ValueKind == JsonValueKind.String && ValidSegment(action.GetString() ?? ""))
            eventType = $"{eventType}.{action.GetString()}";

        return new EventSubmission
        {
            TenantId = source.TenantId, TopicId = source.TopicId, SourceId = source.SourceId,
            SourceEventId = deliveryId, EventType = $"{source.ConnectorKey}.{eventType}", Payload = payload,
            IdempotencyKey = $"{source.SourceId}:{deliveryId}",
        };
    }

    private static string? RequiredHeader(IReadOnlyDictionary<string, string> headers, string name) =>
        headers.TryGetValue(name, out string? value) && !string.IsNullOrWhiteSpace(value) ? value : null;

    private static bool ValidSegment(string value) => value.Length is > 0 and <= 200 && value.All(static ch => char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-');
}
