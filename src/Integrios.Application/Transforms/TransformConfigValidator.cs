using System.Text;
using System.Text.Json;

namespace Integrios.Application.Transforms;

// Shared by Subscription transform and Source-contract mapping authoring: both declare the same
// {engine, version, expression} document shape and reuse the one JSONata evaluator.
internal static class TransformConfigValidator
{
    private const int MaxExpressionBytes = 64 * 1024;

    public static string? Validate(
        JsonElement transform,
        ITransformEvaluator evaluator,
        string fieldName,
        out TransformSpec? transformSpec)
    {
        transformSpec = null;

        if (transform.ValueKind != JsonValueKind.Object)
            return $"{fieldName} must be an object.";

        if (!transform.TryGetProperty("engine", out JsonElement engineElement)
            || engineElement.ValueKind != JsonValueKind.String)
            return $"{fieldName}.engine is required and must be a string.";

        if (!transform.TryGetProperty("version", out JsonElement versionElement)
            || versionElement.ValueKind != JsonValueKind.String)
            return $"{fieldName}.version is required and must be a string.";

        if (!transform.TryGetProperty("expression", out JsonElement expressionElement)
            || expressionElement.ValueKind != JsonValueKind.String)
            return $"{fieldName}.expression is required and must be a string.";

        string engine = engineElement.GetString()!;
        string version = versionElement.GetString()!;
        string expression = expressionElement.GetString()!;

        if (string.IsNullOrWhiteSpace(expression))
            return $"{fieldName}.expression must not be empty.";

        if (Encoding.UTF8.GetByteCount(expression) > MaxExpressionBytes)
            return $"{fieldName}.expression must not exceed 64 KiB of UTF-8 text.";

        transformSpec = new TransformSpec(engine, version, expression);
        return evaluator.ValidateExpression(transformSpec);
    }
}
