using System.Text.Json;
using Integrios.Application;
using Integrios.Application.Transforms;
using Integrios.Application.Subscriptions;
using Integrios.Infrastructure.Transforms;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace Integrios.Application.UnitTests;

public sealed class TransformPreviewTests : IDisposable
{
    private readonly ServiceProvider provider;
    private readonly IMediator mediator;

    public TransformPreviewTests()
    {
        var services = new ServiceCollection();
        services.AddApplicationServices();
        services.AddSingleton<ITransformEvaluator, JsonataTransformEvaluator>();
        provider = services.BuildServiceProvider();
        mediator = provider.GetRequiredService<IMediator>();
    }

    [Fact]
    public async Task Preview_ReturnsTransformedOutput_ForValidExpression()
    {
        PreviewTransformResult result = await RunExpr("{ \"amount\": amount }", "{\"amount\":1200}");

        Assert.Null(result.Error);
        using var document = JsonDocument.Parse(result.OutputJson!);
        Assert.Equal(1200, document.RootElement.GetProperty("amount").GetInt32());
    }

    [Fact]
    public async Task Preview_SurfacesMissingField_ForWrongPayloadPrefix()
    {
        PreviewTransformResult result = await RunExpr(
            "{ \"amount\": payload.amount }",
            "{\"amount\":1200}");

        Assert.Null(result.Error);
        using var document = JsonDocument.Parse(result.OutputJson!);
        Assert.False(document.RootElement.TryGetProperty("amount", out _));
    }

    [Fact]
    public async Task Preview_AppliesContextDefaults_WhenSampleContextOmitted()
    {
        PreviewTransformResult result = await RunExpr("{ \"et\": $context.event_type }", "{}");

        Assert.Null(result.Error);
        using var document = JsonDocument.Parse(result.OutputJson!);
        Assert.Equal("sample.event", document.RootElement.GetProperty("et").GetString());
    }

    [Fact]
    public async Task Preview_UsesProvidedSampleContext()
    {
        PreviewTransformResult result = await RunExpr(
            "{ \"et\": $context.event_type }",
            "{}",
            "{\"event_type\":\"payment.created\"}");

        using var document = JsonDocument.Parse(result.OutputJson!);
        Assert.Equal("payment.created", document.RootElement.GetProperty("et").GetString());
    }

    [Fact]
    public async Task Preview_ReturnsError_ForInvalidSyntax()
    {
        PreviewTransformResult result = await RunExpr("{ \"amount\": ", "{}");

        Assert.NotNull(result.Error);
        Assert.Null(result.OutputJson);
    }

    [Fact]
    public async Task Preview_ReturnsError_ForMissingExpression()
    {
        PreviewTransformResult result = await mediator.Send(new PreviewTransformQuery(
            Json("{\"engine\":\"jsonata\",\"version\":\"1\"}"),
            Json("{}"),
            null));

        Assert.Contains("expression", result.Error);
        Assert.Null(result.OutputJson);
    }

    [Fact]
    public async Task Preview_ReturnsError_ForRuntimeEvaluationError()
    {
        PreviewTransformResult result = await RunExpr(
            "amount + $context.event_type",
            "{\"amount\":1200}");

        Assert.NotNull(result.Error);
        Assert.Null(result.OutputJson);
    }

    [Fact]
    public async Task Preview_PreCanceledRequest_DoesNotEvaluate()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            mediator.Send(
                new PreviewTransformQuery(
                    Json("""{"engine":"jsonata","version":"1","expression":"amount"}"""),
                    Json("{\"amount\":1200}"),
                    null),
                cancellation.Token));
    }

    public void Dispose() => provider.Dispose();

    private Task<PreviewTransformResult> RunExpr(
        string expression,
        string sampleInput,
        string? sampleContext = null)
    {
        string transform =
            $$"""{"engine":"jsonata","version":"1","expression":{{JsonSerializer.Serialize(expression)}}}""";
        return mediator.Send(new PreviewTransformQuery(
            Json(transform),
            Json(sampleInput),
            sampleContext is null ? null : Json(sampleContext)));
    }

    private static JsonElement Json(string raw) =>
        JsonDocument.Parse(raw).RootElement.Clone();
}
