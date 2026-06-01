using System.Text.Json;
using Integrios.Admin.Endpoints;
using Integrios.Infrastructure.Transform;

namespace Integrios.Admin.Tests;

// Pure unit tests for the transform-preview logic
public class TransformPreviewTests
{
    private readonly JsonataTransformEvaluator evaluator = new();

    private static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement.Clone();

    private TransformPreviewResult RunExpr(string expression, string sampleInput, string? sampleContext = null)
    {
        var transform = $$"""{"engine":"jsonata","version":"1","expression":{{JsonSerializer.Serialize(expression)}}}""";
        return TransformPreview.Run(
            Json(transform),
            Json(sampleInput),
            sampleContext is null ? null : Json(sampleContext),
            evaluator);
    }

    [Fact]
    public void Run_ReturnsTransformedOutput_ForValidExpression()
    {
        var result = RunExpr("{ \"amount\": amount }", "{\"amount\":1200}");

        Assert.Null(result.Error);
        using var doc = JsonDocument.Parse(result.OutputJson!);
        Assert.Equal(1200, doc.RootElement.GetProperty("amount").GetInt32());
    }

    [Fact]
    public void Run_SurfacesMissingField_ForWrongPayloadPrefix()
    {
        // The whole point of preview: the author sees `amount` is gone before saving.
        var result = RunExpr("{ \"amount\": payload.amount }", "{\"amount\":1200}");

        Assert.Null(result.Error);
        using var doc = JsonDocument.Parse(result.OutputJson!);
        Assert.False(doc.RootElement.TryGetProperty("amount", out _));
    }

    [Fact]
    public void Run_AppliesContextDefaults_WhenSampleContextOmitted()
    {
        var result = RunExpr("{ \"et\": $context.event_type }", "{}");

        Assert.Null(result.Error);
        using var doc = JsonDocument.Parse(result.OutputJson!);
        Assert.Equal("sample.event", doc.RootElement.GetProperty("et").GetString());
    }

    [Fact]
    public void Run_UsesProvidedSampleContext()
    {
        var result = RunExpr("{ \"et\": $context.event_type }", "{}", "{\"event_type\":\"payment.created\"}");

        using var doc = JsonDocument.Parse(result.OutputJson!);
        Assert.Equal("payment.created", doc.RootElement.GetProperty("et").GetString());
    }

    [Fact]
    public void Run_ReturnsError_ForInvalidSyntax()
    {
        var result = RunExpr("{ \"amount\": ", "{}");

        Assert.NotNull(result.Error);
        Assert.Null(result.OutputJson);
    }

    [Fact]
    public void Run_ReturnsError_ForMissingExpression()
    {
        var result = TransformPreview.Run(
            Json("{\"engine\":\"jsonata\",\"version\":\"1\"}"),
            Json("{}"),
            null,
            evaluator);

        Assert.Contains("expression", result.Error);
    }

    [Fact]
    public void Run_ReturnsError_ForRuntimeEvaluationError()
    {
        var result = RunExpr("amount + $context.event_type", "{\"amount\":1200}");

        Assert.NotNull(result.Error);
        Assert.Null(result.OutputJson);
    }
}
