using System.Text.Json;
using Integrios.Application.Abstractions;

namespace Integrios.Admin.Endpoints;

internal static class TransformConfig
{
    // Validates a non-null transform object (engine/version/expression present + syntax).
    // Returns an error message, or null with the parsed expression on success. Shared by
    // subscription create/update and the transform preview.
    internal static string? Parse(JsonElement transform, ITransformEvaluator evaluator, out string expression)
    {
        expression = "";

        if (transform.ValueKind != JsonValueKind.Object)
            return "transform must be an object.";

        if (!transform.TryGetProperty("engine", out var engineEl) || engineEl.ValueKind != JsonValueKind.String)
            return "transform.engine is required and must be a string.";

        if (!transform.TryGetProperty("version", out var versionEl) || versionEl.ValueKind != JsonValueKind.String)
            return "transform.version is required and must be a string.";

        if (!transform.TryGetProperty("expression", out var expressionEl) || expressionEl.ValueKind != JsonValueKind.String)
            return "transform.expression is required and must be a string.";

        var engine = engineEl.GetString()!;
        var version = versionEl.GetString()!;
        expression = expressionEl.GetString()!;

        if (string.IsNullOrWhiteSpace(expression))
            return "transform.expression must not be empty.";

        return evaluator.ValidateExpression(engine, version, expression);
    }
}
