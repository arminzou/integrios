using System.Text.Json;
using Integrios.Application;
using Integrios.Application.Transforms;
using Integrios.Application.Authoring.Subscriptions;
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
        PreviewMappingResult result = await RunExpr("{ \"amount\": amount }", "{\"amount\":1200}");

        result.Error.ShouldBeNull();
        using var document = JsonDocument.Parse(result.OutputJson!);
        document.RootElement.GetProperty("amount").GetInt32().ShouldBe(1200);
    }

    [Fact]
    public async Task Preview_SurfacesMissingField_ForWrongPayloadPrefix()
    {
        PreviewMappingResult result = await RunExpr(
            "{ \"amount\": payload.amount }",
            "{\"amount\":1200}");

        result.Error.ShouldBeNull();
        using var document = JsonDocument.Parse(result.OutputJson!);
        document.RootElement.TryGetProperty("amount", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task Preview_AppliesContextDefaults_WhenSampleContextOmitted()
    {
        PreviewMappingResult result = await RunExpr("{ \"et\": $context.event_type }", "{}");

        result.Error.ShouldBeNull();
        using var document = JsonDocument.Parse(result.OutputJson!);
        document.RootElement.GetProperty("et").GetString().ShouldBe("sample.event");
    }

    [Fact]
    public async Task Preview_UsesProvidedSampleContext()
    {
        PreviewMappingResult result = await RunExpr(
            "{ \"et\": $context.event_type }",
            "{}",
            "{\"event_type\":\"payment.created\"}");

        using var document = JsonDocument.Parse(result.OutputJson!);
        document.RootElement.GetProperty("et").GetString().ShouldBe("payment.created");
    }

    [Fact]
    public async Task Preview_ReturnsError_ForInvalidSyntax()
    {
        PreviewMappingResult result = await RunExpr("{ \"amount\": ", "{}");

        result.Error.ShouldNotBeNull();
        result.OutputJson.ShouldBeNull();
    }

    [Fact]
    public async Task Preview_ReturnsError_ForMissingExpression()
    {
        PreviewMappingResult result = await mediator.Send(new PreviewMappingQuery(
            Json("{\"engine\":\"jsonata\",\"version\":\"1\"}"),
            Json("{}"),
            null));

        result.Error!.ShouldContain("expression", Case.Sensitive);
        result.OutputJson.ShouldBeNull();
    }

    [Fact]
    public async Task Preview_ReturnsError_ForRuntimeEvaluationError()
    {
        PreviewMappingResult result = await RunExpr(
            "amount + $context.event_type",
            "{\"amount\":1200}");

        result.Error.ShouldNotBeNull();
        result.OutputJson.ShouldBeNull();
    }

    [Fact]
    public async Task Preview_PreCanceledRequest_DoesNotEvaluate()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() =>
            mediator.Send(
                new PreviewMappingQuery(
                    Json("""{"engine":"jsonata","version":"1","expression":"amount"}"""),
                    Json("{\"amount\":1200}"),
                    null),
                cancellation.Token));
    }

    public void Dispose() => provider.Dispose();

    private Task<PreviewMappingResult> RunExpr(
        string expression,
        string sampleInput,
        string? sampleContext = null)
    {
        string transform =
            $$"""{"engine":"jsonata","version":"1","expression":{{JsonSerializer.Serialize(expression)}}}""";
        return mediator.Send(new PreviewMappingQuery(
            Json(transform),
            Json(sampleInput),
            sampleContext is null ? null : Json(sampleContext)));
    }

    private static JsonElement Json(string raw) =>
        JsonDocument.Parse(raw).RootElement.Clone();
}
