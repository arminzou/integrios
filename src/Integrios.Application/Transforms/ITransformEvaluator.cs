namespace Integrios.Application.Transforms;

public sealed record TransformSpec(string Engine, string Version, string Expression);

public interface ITransformEvaluator
{
    string? ValidateExpression(TransformSpec transform);
    string Evaluate(
        TransformSpec transform,
        string payloadJson,
        TransformContext context);
}

public record TransformContext(string EventType, string? TopicName, DateTimeOffset AcceptedAt);

public sealed class TransformEvaluationException(string message) : Exception(message);
