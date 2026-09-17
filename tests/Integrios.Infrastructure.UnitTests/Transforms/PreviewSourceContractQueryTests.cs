using System.Text.Json;
using Integrios.Application;
using Integrios.Application.Authoring.Connectors;
using Integrios.Application.Transforms;
using Integrios.Domain.ValueObjects;
using Integrios.Infrastructure.Transforms;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace Integrios.Infrastructure.UnitTests;

public sealed class PreviewSourceContractQueryTests : IDisposable
{
    private readonly ServiceProvider provider;
    private readonly IMediator mediator;

    public PreviewSourceContractQueryTests()
    {
        var services = new ServiceCollection();
        services.AddAdminApplicationServices();
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
            null,
            null));

        result.Error!.ShouldContain("amount", Case.Sensitive);
        result.OutputJson.ShouldBeNull();
        // The Source's input requirements refused it, not the mapping.
        result.RefusedBy.ShouldBe("schema");
    }

    [Fact]
    public async Task Preview_ReturnsError_ForInvalidMappingSyntax()
    {
        PreviewSourceContractResult result = await RunMapping("{ \"event_type\": ", "{}");

        result.Error.ShouldNotBeNull();
        result.RefusedBy.ShouldBe("mapping");
        result.OutputJson.ShouldBeNull();
    }

    // The identity rule runs before the mapping and decides whether the input is admitted at all, so
    // a preview that showed only the mapping output would not be showing the Event that is accepted.
    [Fact]
    public async Task Preview_ResolvesSourceEventId_FromASampleRequestHeader()
    {
        PreviewSourceContractResult result = await RunMapping(
            "{ \"event_type\": \"a\", \"payload\": $ }",
            "{}",
            """{"headers":{"x-github-delivery":"d-7"}}""",
            new SourceEventIdentityRule { Kind = "header", Value = "X-GitHub-Delivery" });

        result.Error.ShouldBeNull();
        result.SourceEventId.ShouldBe("d-7");
    }

    [Fact]
    public async Task Preview_ResolvesSourceEventId_FromAJsonPointerIntoTheSample()
    {
        PreviewSourceContractResult result = await RunMapping(
            "{ \"event_type\": \"a\", \"payload\": $ }",
            """{"delivery":{"id":"d-9"}}""",
            """{"headers":{}}""",
            new SourceEventIdentityRule { Kind = "json_path", Value = "/delivery/id" });

        result.Error.ShouldBeNull();
        result.SourceEventId.ShouldBe("d-9");
    }

    [Fact]
    public async Task Preview_ReportsTheRejection_WhenTheSampleCarriesNoIdentity()
    {
        PreviewSourceContractResult result = await RunMapping(
            "{ \"event_type\": \"a\", \"payload\": $ }",
            "{}",
            """{"headers":{}}""",
            new SourceEventIdentityRule { Kind = "header", Value = "X-GitHub-Delivery" });

        // The refusal names what it looked for, so an Operator can see which header the sample lacks.
        result.Error.ShouldNotBeNull();
        result.Error.ShouldContain("'X-GitHub-Delivery' header");
        result.RefusedBy.ShouldBe("event_identity_rule");
        result.OutputJson.ShouldBeNull();
        result.SourceEventId.ShouldBeNull();
    }

    [Fact]
    public async Task Preview_NamesTheJsonPointer_WhenTheSampleCarriesNoIdentityThere()
    {
        PreviewSourceContractResult result = await RunMapping(
            "{ \"event_type\": \"a\", \"payload\": $ }",
            """{"delivery":{"id":7}}""",
            """{"headers":{}}""",
            new SourceEventIdentityRule { Kind = "json_path", Value = "/delivery/id" });

        result.Error.ShouldNotBeNull();
        result.Error.ShouldContain("'/delivery/id'");
        result.RefusedBy.ShouldBe("event_identity_rule");
    }

    [Fact]
    public async Task Preview_AcceptsAMissingIdentity_WhenTheRulePermitsOne()
    {
        PreviewSourceContractResult result = await RunMapping(
            "{ \"event_type\": \"a\", \"payload\": $ }",
            "{}",
            """{"headers":{}}""",
            new SourceEventIdentityRule { Kind = "header", Value = "X-GitHub-Delivery", AllowMissing = true });

        result.Error.ShouldBeNull();
        result.SourceEventId.ShouldBeNull();
    }

    // A broker supplies the message id with the message, so no sample can carry one. That is not the
    // same as a rule whose value is absent, and it is not reported as a rejection.
    [Fact]
    public async Task Preview_LeavesABrokerMessageIdUnresolved_RatherThanRefusingTheSample()
    {
        PreviewSourceContractResult result = await RunMapping(
            "{ \"event_type\": \"a\", \"payload\": $ }",
            "{}",
            null,
            new SourceEventIdentityRule { Kind = "message_id", Value = "message_id" });

        result.Error.ShouldBeNull();
        result.OutputJson.ShouldNotBeNull();
        result.SourceEventId.ShouldBeNull();
    }

    [Fact]
    public async Task Preview_RefusesARuleTheSourceCouldNotBeAuthoredWith()
    {
        PreviewSourceContractResult result = await RunMapping(
            "{ \"event_type\": \"a\", \"payload\": $ }",
            "{}",
            """{"headers":{}}""",
            new SourceEventIdentityRule { Kind = "json_path", Value = "delivery.id" });

        result.Error.ShouldNotBeNull();
        result.Error.ShouldContain("JSON Pointer");
    }

    // A Source with no mapping takes its input as the Event. A provider's own JSON is not one, and
    // the preview has to say so rather than decline to check.
    [Fact]
    public async Task Preview_RefusesAProviderPayload_WhenTheSourceHasNoMapping()
    {
        PreviewSourceContractResult result = await RunUnmapped("""{"ref":"refs/heads/main"}""");

        result.Error.ShouldNotBeNull();
        // With no mapping it is the input itself that is not an Event.
        result.RefusedBy.ShouldBe("sample_input");
        result.OutputJson.ShouldBeNull();
    }

    [Fact]
    public async Task Preview_AcceptsAnIntegriosEvent_WhenTheSourceHasNoMapping()
    {
        PreviewSourceContractResult result = await RunUnmapped(
            """{"event_type":"order.placed","source_event_id":"o-1","payload":{"id":1}}""");

        result.Error.ShouldBeNull();
        using var document = JsonDocument.Parse(result.OutputJson!);
        document.RootElement.GetProperty("event_type").GetString().ShouldBe("order.placed");
        // With no rule, ingestion takes the identity the Event itself carries.
        result.SourceEventId.ShouldBe("o-1");
    }

    private Task<PreviewSourceContractResult> RunUnmapped(string sampleInput) =>
        mediator.Send(new PreviewSourceContractQuery(null, null, Json(sampleInput), Json("""{"headers":{}}"""), null));

    public void Dispose() => provider.Dispose();

    private Task<PreviewSourceContractResult> RunMapping(
        string expression,
        string sampleInput,
        string? sampleContext = null,
        SourceEventIdentityRule? identityRule = null)
    {
        string mapping =
            $$"""{"engine":"jsonata","version":"1","expression":{{JsonSerializer.Serialize(expression)}}}""";
        return mediator.Send(new PreviewSourceContractQuery(
            null,
            Json(mapping),
            Json(sampleInput),
            sampleContext is null ? null : Json(sampleContext),
            identityRule));
    }

    private static JsonElement Json(string raw) =>
        JsonDocument.Parse(raw).RootElement.Clone();
}
