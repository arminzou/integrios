using Integrios.Application.Abstractions;
using Jsonata.Net.Native;
using Jsonata.Net.Native.Json;

namespace Integrios.Infrastructure.Transform;

public sealed class JsonataTransformEvaluator : ITransformEvaluator
{
    public string? ValidateExpression(string engine, string version, string expression)
    {
        string? unsupportedPair = ValidateEngineVersion(engine, version);
        if (unsupportedPair is not null)
            return unsupportedPair;

        try
        {
            _ = new JsonataQuery(expression);
            return null;
        }
        catch (Exception ex)
        {
            return $"Invalid JSONata expression: {ex.Message}";
        }
    }

    public string Evaluate(
        string engine,
        string version,
        string expression,
        string payloadJson,
        TransformContext context)
    {
        string? unsupportedPair = ValidateEngineVersion(engine, version);
        if (unsupportedPair is not null)
            throw new TransformEvaluationException(unsupportedPair);

        JsonataQuery query;
        try
        {
            query = new JsonataQuery(expression);
        }
        catch (Exception ex)
        {
            throw new TransformEvaluationException($"Failed to compile transform expression: {ex.Message}");
        }

        JToken payload;
        try
        {
            payload = JToken.Parse(payloadJson);
        }
        catch (Exception ex)
        {
            throw new TransformEvaluationException($"Failed to parse event payload for transform: {ex.Message}");
        }

        var contextJson = $"{{\"event_type\":{Quoted(context.EventType)},\"topic_name\":{Quoted(context.TopicName)},\"accepted_at\":{Quoted(context.AcceptedAt.ToString("o"))}}}";
        var contextToken = JToken.Parse(contextJson);

        var env = new EvaluationEnvironment();
        env.BindValue("context", contextToken);

        JToken result;
        try
        {
            result = query.Eval(payload, env);
        }
        catch (Exception ex)
        {
            throw new TransformEvaluationException($"Transform evaluation failed: {ex.Message}");
        }

        return result.ToFlatString();
    }

    private static string? ValidateEngineVersion(string engine, string version)
    {
        if (!string.Equals(engine, "jsonata", StringComparison.OrdinalIgnoreCase))
            return $"Unsupported engine '{engine}'. Only 'jsonata' is supported.";

        return version != "1"
            ? $"Unsupported version '{version}'. Only '1' is supported."
            : null;
    }

    private static string Quoted(string? value) =>
        value is null ? "null" : $"\"{value.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";
}
