using System.Text.Json;

namespace Integrios.Application.Transforms;

// Shared by Source-contract preview and the runtime Event API/webhook/queue acceptance paths: both
// must enforce the same strictly bounded output shape, whether the JSON came from a Source mapping
// or (with no mapping declared) directly from the caller's input document.
public static class SourceMappingOutputValidator
{
    private static readonly HashSet<string> AllowedOutputFields =
        ["event_type", "source_event_id", "payload", "metadata"];

    public static SourceContractOutput Validate(string outputJson)
    {
        JsonElement output;
        try
        {
            output = JsonSerializer.Deserialize<JsonElement>(outputJson);
        }
        catch (JsonException exception)
        {
            throw new TransformEvaluationException($"Source mapping output must be valid JSON: {exception.Message}");
        }

        if (output.ValueKind != JsonValueKind.Object)
            throw new TransformEvaluationException("Source mapping output must be a JSON object.");

        foreach (JsonProperty property in output.EnumerateObject())
        {
            if (!AllowedOutputFields.Contains(property.Name))
                throw new TransformEvaluationException(
                    $"Source mapping output contains unsupported field '{property.Name}'.");
        }

        if (!output.TryGetProperty("event_type", out JsonElement eventType)
            || eventType.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(eventType.GetString()))
        {
            throw new TransformEvaluationException("Source mapping output must include a non-empty 'event_type' string.");
        }

        if (!output.TryGetProperty("payload", out JsonElement payload))
            throw new TransformEvaluationException("Source mapping output must include 'payload'.");

        string? sourceEventId = null;
        if (output.TryGetProperty("source_event_id", out JsonElement sourceEventIdElement))
        {
            if (sourceEventIdElement.ValueKind != JsonValueKind.String)
                throw new TransformEvaluationException("Source mapping output 'source_event_id' must be a string when present.");
            sourceEventId = sourceEventIdElement.GetString();
        }

        JsonElement? metadata = null;
        if (output.TryGetProperty("metadata", out JsonElement metadataElement))
        {
            if (metadataElement.ValueKind != JsonValueKind.Object)
                throw new TransformEvaluationException("Source mapping output 'metadata' must be an object when present.");
            metadata = metadataElement;
        }

        return new SourceContractOutput(eventType.GetString()!, sourceEventId, payload, metadata);
    }
}

public sealed record SourceContractOutput(string EventType, string? SourceEventId, JsonElement Payload, JsonElement? Metadata);
