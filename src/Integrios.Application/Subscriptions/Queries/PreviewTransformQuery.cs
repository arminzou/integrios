using System.Text.Json;
using Integrios.Application.Transforms;
using MediatR;

namespace Integrios.Application.Subscriptions;

public sealed record PreviewTransformQuery(
    JsonElement Transform,
    JsonElement SampleInput,
    JsonElement? SampleContext) : IRequest<PreviewTransformResult>;

public sealed record PreviewTransformResult(string? Error, string? OutputJson);

internal sealed class PreviewTransformQueryHandler(ITransformEvaluator evaluator)
    : IRequestHandler<PreviewTransformQuery, PreviewTransformResult>
{
    public Task<PreviewTransformResult> Handle(
        PreviewTransformQuery query,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string? error = TransformConfigValidator.Validate(
            query.Transform,
            evaluator,
            "transform",
            out TransformSpec? transform);
        if (error is not null)
            return Task.FromResult(new PreviewTransformResult(error, null));
        if (transform is null)
            throw new InvalidOperationException("Transform validation succeeded without a parsed transform.");

        string inputJson = query.SampleInput.ValueKind == JsonValueKind.Undefined
            ? "{}"
            : query.SampleInput.GetRawText();
        TransformContext context = BuildContext(query.SampleContext);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new PreviewTransformResult(
                null,
                evaluator.Evaluate(transform, inputJson, context)));
        }
        catch (TransformEvaluationException exception)
        {
            return Task.FromResult(new PreviewTransformResult(exception.Message, null));
        }
    }

    private static TransformContext BuildContext(JsonElement? sampleContext)
    {
        string eventType = "sample.event";
        string? topicName = "sample-topic";
        DateTimeOffset acceptedAt = DateTimeOffset.UtcNow;

        if (sampleContext is { ValueKind: JsonValueKind.Object } context)
        {
            if (context.TryGetProperty("event_type", out JsonElement eventTypeElement)
                && eventTypeElement.ValueKind == JsonValueKind.String)
                eventType = eventTypeElement.GetString()!;

            if (context.TryGetProperty("topic_name", out JsonElement topicNameElement)
                && topicNameElement.ValueKind == JsonValueKind.String)
                topicName = topicNameElement.GetString();

            if (context.TryGetProperty("accepted_at", out JsonElement acceptedAtElement)
                && acceptedAtElement.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(acceptedAtElement.GetString(), out DateTimeOffset parsed))
                acceptedAt = parsed;
        }

        return new TransformContext(eventType, topicName, acceptedAt);
    }
}
