using System.Text.Json;
using Integrios.Application;
using Integrios.Application.Authoring.Connectors;
using Integrios.Application.Transforms;
using Integrios.Infrastructure.Transforms;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace Integrios.Application.UnitTests;

public sealed class PreviewSourceContractQueryTests : IDisposable
{
    private readonly ServiceProvider provider;
    private readonly IMediator mediator;

    public PreviewSourceContractQueryTests()
    {
        var services = new ServiceCollection();
        services.AddApplicationServices();
        services.AddSingleton<ITransformEvaluator, JsonataTransformEvaluator>();
        provider = services.BuildServiceProvider();
        mediator = provider.GetRequiredService<IMediator>();
    }

    [Fact]
    public async Task Preview_ReturnsRestrictedOutput_ForValidMapping()
    {
        PreviewSourceContractResult result = await RunMapping(
            "{ \"event_type\": \"payment.created\", \"payload\": $ }",
            "{\"amount\":1200}");

        Assert.Null(result.Error);
        using var document = JsonDocument.Parse(result.OutputJson!);
        Assert.Equal("payment.created", document.RootElement.GetProperty("event_type").GetString());
        Assert.Equal(1200, document.RootElement.GetProperty("payload").GetProperty("amount").GetInt32());
        Assert.False(document.RootElement.TryGetProperty("source_event_id", out _));
        Assert.False(document.RootElement.TryGetProperty("metadata", out _));
    }

    [Fact]
    public async Task Preview_UsesProvidedSampleContext()
    {
        PreviewSourceContractResult result = await RunMapping(
            "{ \"event_type\": $context.event_type, \"payload\": $ }",
            "{}",
            """{"event_type":"dataverse.contact.updated"}""");

        Assert.Null(result.Error);
        using var document = JsonDocument.Parse(result.OutputJson!);
        Assert.Equal("dataverse.contact.updated", document.RootElement.GetProperty("event_type").GetString());
    }

    [Fact]
    public async Task Preview_RejectsOutputWithUnsupportedField()
    {
        PreviewSourceContractResult result = await RunMapping(
            "{ \"event_type\": \"x\", \"payload\": $, \"routing_key\": \"nope\" }",
            "{}");

        Assert.Contains("unsupported field 'routing_key'", result.Error, StringComparison.Ordinal);
        Assert.Null(result.OutputJson);
    }

    [Fact]
    public async Task Preview_RejectsOutputMissingEventType()
    {
        PreviewSourceContractResult result = await RunMapping(
            "{ \"payload\": $ }",
            "{}");

        Assert.Contains("event_type", result.Error, StringComparison.Ordinal);
        Assert.Null(result.OutputJson);
    }

    [Fact]
    public async Task Preview_RejectsSampleInputThatFailsDeclaredSchema()
    {
        PreviewSourceContractResult result = await mediator.Send(new PreviewSourceContractQuery(
            Json("""{"type":"object","properties":{"amount":{"type":"integer"}},"required":["amount"],"additionalProperties":false}"""),
            Json("""{"engine":"jsonata","version":"1","expression":"{ \"event_type\": \"x\", \"payload\": $ }"}"""),
            Json("{}"),
            null));

        Assert.Contains("amount", result.Error, StringComparison.Ordinal);
        Assert.Null(result.OutputJson);
    }

    [Fact]
    public async Task Preview_ReturnsError_ForInvalidMappingSyntax()
    {
        PreviewSourceContractResult result = await RunMapping("{ \"event_type\": ", "{}");

        Assert.NotNull(result.Error);
        Assert.Null(result.OutputJson);
    }

    public void Dispose() => provider.Dispose();

    private Task<PreviewSourceContractResult> RunMapping(
        string expression,
        string sampleInput,
        string? sampleContext = null)
    {
        string mapping =
            $$"""{"engine":"jsonata","version":"1","expression":{{JsonSerializer.Serialize(expression)}}}""";
        return mediator.Send(new PreviewSourceContractQuery(
            null,
            Json(mapping),
            Json(sampleInput),
            sampleContext is null ? null : Json(sampleContext)));
    }

    private static JsonElement Json(string raw) =>
        JsonDocument.Parse(raw).RootElement.Clone();
}
