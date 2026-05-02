namespace Integrios.Application.Abstractions;

public interface ITransformEvaluator
{
    string? ValidateExpression(string engine, string version, string expression);
    string Evaluate(string expression, string payloadJson, TransformContext context);
}

public record TransformContext(string EventType, string? TopicName, DateTimeOffset AcceptedAt);

public sealed class TransformEvaluationException(string message) : Exception(message);
