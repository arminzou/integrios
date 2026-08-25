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

        result.Error.ShouldBeNull();
        using var document = JsonDocument.Parse(result.OutputJson!);
        document.RootElement.GetProperty("event_type").GetString().ShouldBe("payment.created");
        document.RootElement.GetProperty("payload").GetProperty("amount").GetInt32().ShouldBe(1200);
        document.RootElement.TryGetProperty("source_event_id", out _).ShouldBeFalse();
        document.RootElement.TryGetProperty("metadata", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task Preview_UsesProvidedSampleContext()
    {
        PreviewSourceContractResult result = await RunMapping(
            "{ \"event_type\": $context.event_type, \"payload\": $ }",
            "{}",
            """{"event_type":"dataverse.contact.updated"}""");

        result.Error.ShouldBeNull();
        using var document = JsonDocument.Parse(result.OutputJson!);
        document.RootElement.GetProperty("event_type").GetString().ShouldBe("dataverse.contact.updated");
    }

    [Fact]
    public async Task Preview_RejectsOutputWithUnsupportedField()
    {
        PreviewSourceContractResult result = await RunMapping(
            "{ \"event_type\": \"x\", \"payload\": $, \"routing_key\": \"nope\" }",
            "{}");

        result.Error!.ShouldContain("unsupported field 'routing_key'", Case.Sensitive);
        result.OutputJson.ShouldBeNull();
    }

    [Fact]
    public async Task Preview_RejectsOutputMissingEventType()
    {
        PreviewSourceContractResult result = await RunMapping(
            "{ \"payload\": $ }",
            "{}");

        result.Error!.ShouldContain("event_type", Case.Sensitive);
        result.OutputJson.ShouldBeNull();
    }

    [Fact]
    public async Task Preview_RejectsSampleInputThatFailsDeclaredSchema()
    {
        PreviewSourceContractResult result = await mediator.Send(new PreviewSourceContractQuery(
            Json("""{"type":"object","properties":{"amount":{"type":"integer"}},"required":["amount"],"additionalProperties":false}"""),
            Json("""{"engine":"jsonata","version":"1","expression":"{ \"event_type\": \"x\", \"payload\": $ }"}"""),
            Json("{}"),
            null));

        result.Error!.ShouldContain("amount", Case.Sensitive);
        result.OutputJson.ShouldBeNull();
    }

    [Fact]
    public async Task Preview_ReturnsError_ForInvalidMappingSyntax()
    {
        PreviewSourceContractResult result = await RunMapping("{ \"event_type\": ", "{}");

        result.Error.ShouldNotBeNull();
        result.OutputJson.ShouldBeNull();
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
