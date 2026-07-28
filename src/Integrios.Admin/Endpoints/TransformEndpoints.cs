using System.Text.Json;
using Integrios.Application.Abstractions;
using Integrios.Application.Subscriptions;

namespace Integrios.Admin.Endpoints;

public sealed class TransformEndpoints : IEndpointGroup
{
    public string Prefix => "/transform";

    public void Map(RouteGroupBuilder group)
    {
        group.MapPost(PreviewTransform, "/preview");
    }

    // Stateless dry-run: evaluate a transform against a sample payload so an author can see the
    // output before saving. No tenant data is read, so any authenticated admin may call it.
    private static IResult PreviewTransform(TransformPreviewRequest request, ITransformEvaluator evaluator)
    {
        var result = TransformPreview.Run(request.Transform, request.SampleInput, request.SampleContext, evaluator);
        if (result.Error is not null)
            return Results.BadRequest(new { error = result.Error });

        using var doc = JsonDocument.Parse(result.OutputJson!);
        return Results.Ok(new { output = doc.RootElement.Clone() });
    }
}

internal sealed record TransformPreviewRequest(
    JsonElement Transform,
    JsonElement SampleInput,
    JsonElement? SampleContext);

internal sealed record TransformPreviewResult(string? Error, string? OutputJson);

internal static class TransformPreview
{
    public static TransformPreviewResult Run(
        JsonElement transform,
        JsonElement sampleInput,
        JsonElement? sampleContext,
        ITransformEvaluator evaluator)
    {
        var error = TransformConfigValidator.Validate(transform, evaluator, out var expression);
        if (error is not null)
            return new TransformPreviewResult(error, null);

        var inputJson = sampleInput.ValueKind == JsonValueKind.Undefined ? "{}" : sampleInput.GetRawText();
        var context = BuildContext(sampleContext);

        try
        {
            return new TransformPreviewResult(null, evaluator.Evaluate(expression, inputJson, context));
        }
        catch (TransformEvaluationException ex)
        {
            return new TransformPreviewResult(ex.Message, null);
        }
    }

    // Sample context with sensible defaults; any field the caller supplies overrides its default.
    private static TransformContext BuildContext(JsonElement? sampleContext)
    {
        var eventType = "sample.event";
        string? topicName = "sample-topic";
        var acceptedAt = DateTimeOffset.UtcNow;

        if (sampleContext is { ValueKind: JsonValueKind.Object } ctx)
        {
            if (ctx.TryGetProperty("event_type", out var et) && et.ValueKind == JsonValueKind.String)
                eventType = et.GetString()!;
            if (ctx.TryGetProperty("topic_name", out var tn) && tn.ValueKind == JsonValueKind.String)
                topicName = tn.GetString();
            if (ctx.TryGetProperty("accepted_at", out var at) && at.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(at.GetString(), out var parsed))
                acceptedAt = parsed;
        }

        return new TransformContext(eventType, topicName, acceptedAt);
    }
}
