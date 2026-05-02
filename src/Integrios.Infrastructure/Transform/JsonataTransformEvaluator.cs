using Integrios.Application.Abstractions;
using Jsonata.Net.Native;
using Jsonata.Net.Native.Json;

namespace Integrios.Infrastructure.Transform;

public sealed class JsonataTransformEvaluator : ITransformEvaluator
{
    public string? ValidateExpression(string engine, string version, string expression)
    {
        if (!string.Equals(engine, "jsonata", StringComparison.OrdinalIgnoreCase))
            return $"Unsupported engine '{engine}'. Only 'jsonata' is supported.";

        if (version != "1")
            return $"Unsupported version '{version}'. Only '1' is supported.";

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

    public string Evaluate(string expression, string payloadJson, TransformContext context)
    {
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

    private static string Quoted(string? value) =>
        value is null ? "null" : $"\"{value.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";
}
