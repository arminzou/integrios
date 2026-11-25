using System.Text.Json;
using Integrios.Application.Transforms;

namespace Integrios.Application.Delivery;

internal static class DeliveryMapping
{
    public static (string? Payload, string? Error) Apply(
        ITransformEvaluator transformEvaluator,
        string? mappingConfigSnapshot,
        string payloadJson,
        TransformContext context)
    {
        if (string.IsNullOrWhiteSpace(mappingConfigSnapshot))
            return (payloadJson, null);

        try
        {
            using var doc = JsonDocument.Parse(mappingConfigSnapshot);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("engine", out JsonElement engineEl) || engineEl.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("version", out JsonElement versionEl) || versionEl.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("expression", out JsonElement expressionEl) || expressionEl.ValueKind != JsonValueKind.String)
            {
                return (null, "Transform config is missing required string fields (engine, version, expression).");
            }

            var transform = new TransformSpec(engineEl.GetString()!, versionEl.GetString()!, expressionEl.GetString()!);
            return (transformEvaluator.Evaluate(transform, payloadJson, context), null);
        }
        catch (TransformEvaluationException ex)
        {
            return (null, ex.Message);
        }
        catch (JsonException ex)
        {
            return (null, $"Transform config is not valid JSON: {ex.Message}");
        }
    }
}
