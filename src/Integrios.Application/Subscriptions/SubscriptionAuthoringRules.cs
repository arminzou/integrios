using System.Text;
using System.Text.Json;
using Integrios.Application.Transforms;

namespace Integrios.Application.Subscriptions;

internal static class SubscriptionAuthoringRules
{
    private const string InvalidMatchRulesMessage =
        "matchRules must be an object with exactly one non-empty string property: event_type";

    public static void Validate(
        JsonElement matchRules,
        JsonElement? transformConfig,
        ITransformEvaluator transformEvaluator)
    {
        if (!HasValidMatchRulesShape(matchRules))
            throw new SubscriptionValidationException(InvalidMatchRulesMessage);

        if (transformConfig is null || transformConfig.Value.ValueKind == JsonValueKind.Null)
            return;

        string? error = TransformConfigValidator.Validate(
            transformConfig.Value,
            transformEvaluator,
            out _);
        if (error is not null)
            throw new SubscriptionValidationException(error);
    }

    private static bool HasValidMatchRulesShape(JsonElement matchRules)
    {
        if (matchRules.ValueKind != JsonValueKind.Object)
            return false;

        var enumerator = matchRules.EnumerateObject();
        if (!enumerator.MoveNext())
            return false;

        JsonProperty property = enumerator.Current;
        return property.Name == "event_type"
            && !enumerator.MoveNext()
            && property.Value.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(property.Value.GetString());
    }
}

internal static class TransformConfigValidator
{
    private const int MaxExpressionBytes = 64 * 1024;

    public static string? Validate(
        JsonElement transform,
        ITransformEvaluator evaluator,
        out TransformSpec? transformSpec)
    {
        transformSpec = null;

        if (transform.ValueKind != JsonValueKind.Object)
            return "transform must be an object.";

        if (!transform.TryGetProperty("engine", out JsonElement engineElement)
            || engineElement.ValueKind != JsonValueKind.String)
            return "transform.engine is required and must be a string.";

        if (!transform.TryGetProperty("version", out JsonElement versionElement)
            || versionElement.ValueKind != JsonValueKind.String)
            return "transform.version is required and must be a string.";

        if (!transform.TryGetProperty("expression", out JsonElement expressionElement)
            || expressionElement.ValueKind != JsonValueKind.String)
            return "transform.expression is required and must be a string.";

        string engine = engineElement.GetString()!;
        string version = versionElement.GetString()!;
        string expression = expressionElement.GetString()!;

        if (string.IsNullOrWhiteSpace(expression))
            return "transform.expression must not be empty.";

        if (Encoding.UTF8.GetByteCount(expression) > MaxExpressionBytes)
            return "transform.expression must not exceed 64 KiB of UTF-8 text.";

        transformSpec = new TransformSpec(engine, version, expression);
        return evaluator.ValidateExpression(transformSpec);
    }
}
