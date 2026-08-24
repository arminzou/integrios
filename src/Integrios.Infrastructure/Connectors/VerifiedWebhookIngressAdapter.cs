using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Integrios.Application.Events;
using Integrios.Application.Connectors;
using Integrios.Application.Secrets;

namespace Integrios.Infrastructure.Connectors;

// The generic verified-webhook v1 contract (ADR-0036 amendment): verify HMAC-SHA256 over the exact
// raw body before parsing, then derive delivery identity and Event type from data the Connector
// manifest supplies. No provider-specific behavior lives here; GitHub v1 is just a manifest that
// selects this adapter.
internal sealed class VerifiedWebhookIngressAdapter(ISourceVerificationSecretResolver secretResolver)
    : IIngressSourceAdapter
{
    private const int MaxEventTypeSegmentLength = 200;

    public string Key => "verified_webhook";

    public int ContractVersion => 1;

    public async Task<EventSubmission> ExecuteAsync(
        SourceAdapterExecutionContext context,
        CancellationToken cancellationToken)
    {
        VerifiedWebhookConfig config = VerifiedWebhookConfig.Parse(context.AdapterConfig);

        if (context.ContentType is null
            || !context.ContentType.Split(';')[0].Trim().Equals("application/json", StringComparison.OrdinalIgnoreCase))
        {
            throw new WebhookPayloadException("The request Content-Type must be application/json.");
        }

        string signatureHeader = RequireHeader(context.Headers, config.SignatureHeader)
            ?? throw new SourceVerificationException($"Missing required signature header '{config.SignatureHeader}'.");

        string signatureValue = signatureHeader;
        if (config.SignaturePrefix is { } prefix)
        {
            if (!signatureValue.StartsWith(prefix, StringComparison.Ordinal))
                throw new SourceVerificationException("Signature header does not carry the expected prefix.");
            signatureValue = signatureValue[prefix.Length..];
        }

        byte[] providedSignature;
        try
        {
            providedSignature = config.SignatureEncoding == "hex"
                ? Convert.FromHexString(signatureValue)
                : Convert.FromBase64String(signatureValue);
        }
        catch (FormatException)
        {
            throw new SourceVerificationException("Signature header is not validly encoded.");
        }

        string secretReference = context.SourceVerification.SecretRefs.GetProperty("secret").GetString()
            ?? throw new SourceVerificationException("The source Connection has no configured verification secret.");
        string secret = await secretResolver.ResolveAsync(
            new TenantSecretScope(context.TenantId, context.TenantSlug), secretReference, cancellationToken);

        byte[] expectedSignature = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), context.RawBody.Span);
        if (!CryptographicOperations.FixedTimeEquals(providedSignature, expectedSignature))
            throw new SourceVerificationException("Signature verification failed.");

        JsonElement payload;
        try
        {
            payload = JsonSerializer.Deserialize<JsonElement>(context.RawBody.Span);
        }
        catch (JsonException)
        {
            throw new WebhookPayloadException("The request body must be valid JSON.");
        }

        if (payload.ValueKind != JsonValueKind.Object)
            throw new WebhookPayloadException("The request body must be a JSON object.");

        string deliveryId = RequireHeader(context.Headers, config.DeliveryIdHeader)
            ?? throw new WebhookPayloadException($"Missing required delivery identity header '{config.DeliveryIdHeader}'.");

        string eventTypeSegment = RequireHeader(context.Headers, config.EventTypeHeader)
            ?? throw new WebhookPayloadException($"Missing required event-type header '{config.EventTypeHeader}'.");
        if (!IsValidSegment(eventTypeSegment))
            throw new WebhookPayloadException($"Header '{config.EventTypeHeader}' does not carry a valid event-type segment.");

        // Unknown but valid provider actions are accepted under their derived type; a missing or
        // non-string action field simply yields no action segment (e.g. GitHub's push event).
        string eventType = $"{context.ConnectorKey}.{eventTypeSegment}";
        if (config.EventTypeActionField is { } actionField
            && payload.TryGetProperty(actionField, out JsonElement actionValue)
            && actionValue.ValueKind == JsonValueKind.String
            && actionValue.GetString() is { Length: > 0 } action
            && IsValidSegment(action))
        {
            eventType = $"{eventType}.{action}";
        }

        return new EventSubmission
        {
            TenantId = context.TenantId,
            TopicId = context.TopicId,
            SourceId = context.SourceId,
            SourceEventId = deliveryId,
            EventType = eventType,
            Payload = payload,
            // Namespaced by source endpoint so identical delivery ids on different endpoints never collide.
            IdempotencyKey = $"{context.EndpointId}:{deliveryId}",
        };
    }

    private static string? RequireHeader(IReadOnlyDictionary<string, string> headers, string name) =>
        headers.TryGetValue(name, out string? value) && !string.IsNullOrWhiteSpace(value) ? value : null;

    private static bool IsValidSegment(string value) =>
        value.Length <= MaxEventTypeSegmentLength
        && value.All(static ch => char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-');

    private sealed record VerifiedWebhookConfig(
        string SignatureHeader,
        string SignatureEncoding,
        string? SignaturePrefix,
        string DeliveryIdHeader,
        string EventTypeHeader,
        string? EventTypeActionField)
    {
        public static VerifiedWebhookConfig Parse(JsonElement config) => new(
            config.GetProperty("signature_header").GetString()!,
            config.GetProperty("signature_encoding").GetString()!,
            config.TryGetProperty("signature_prefix", out JsonElement prefix) ? prefix.GetString() : null,
            config.GetProperty("delivery_id_header").GetString()!,
            config.GetProperty("event_type_header").GetString()!,
            config.TryGetProperty("event_type_action_field", out JsonElement action) ? action.GetString() : null);
    }
}
