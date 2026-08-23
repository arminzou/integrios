using System.Text.Json;

namespace Integrios.Application.Transforms;

public sealed record TransformSpec(string Engine, string Version, string Expression);

public interface ITransformEvaluator
{
    string? ValidateExpression(TransformSpec transform);
    string Evaluate(
        TransformSpec transform,
        string payloadJson,
        TransformContext context);

    // Used where the bound "$context" is a caller-supplied bag rather than the fixed Subscription
    // {event_type, topic_name, accepted_at} shape (e.g. Source-contract mapping preview).
    string Evaluate(
        TransformSpec transform,
        string payloadJson,
        JsonElement? context);
}

public record TransformContext(string EventType, string? TopicName, DateTimeOffset AcceptedAt);

public sealed class TransformEvaluationException(string message) : Exception(message);
