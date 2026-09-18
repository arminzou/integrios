using System.Text.Json;
using Integrios.Application.Authoring.Sources;
using Integrios.Application.Common;
using Integrios.Application.Ingestion;
using Integrios.Application.Transforms;
using Integrios.Domain.Enums;
using Integrios.Domain.ValueObjects;
using MediatR;

namespace Integrios.Application.Authoring.Connectors;

public sealed record PreviewSourceContractQuery(
    JsonElement? Schema,
    JsonElement? Mapping,
    JsonElement SampleInput,
    JsonElement? SampleContext,
    SourceEventIdentityRule? EventIdentityRule,
    IReadOnlyList<string>? EventTypes = null) : IRequest<PreviewSourceContractResult>;

// SourceEventId is what the rule selects from this sample, so the preview shows the Event as
// Ingestion would accept it rather than the mapping output alone. Null where the rule reads
// something a sample cannot carry -- a broker message id -- or where an absent value is permitted.
//
// RefusedBy names the request field whose part of the check refused the sample, the way a
// validation failure is keyed everywhere else in the Admin API: event_identity_rule, schema (the
// Source's input requirements), mapping, sample_input (with no mapping, the input is not itself an
// Event), or event_types (the output's Event type is not one the Source declares). A caller explains
// a refusal from that rather than from the wording of the message.
public sealed record PreviewSourceContractResult(
    string? Error,
    string? RefusedBy,
    string? OutputJson,
    string? SourceEventId)
{
    public static PreviewSourceContractResult Refused(string refusedBy, string error) => new(error, refusedBy, null, null);
}

internal sealed class PreviewSourceContractQueryHandler(ITransformEvaluator evaluator)
    : IRequestHandler<PreviewSourceContractQuery, PreviewSourceContractResult>
{
    public Task<PreviewSourceContractResult> Handle(
        PreviewSourceContractQuery query,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // A Source with no request context is a broker Source: that is the same distinction the
        // authoring form makes, and it decides which identity kinds are valid.
        SourceType sourceType = query.SampleContext is null ? SourceType.Broker : SourceType.Webhook;
        string? sourceEventId;
        try
        {
            sourceEventId = ResolveIdentity(query, sourceType);
        }
        catch (SourceValidationException exception)
        {
            return Refused("event_identity_rule", exception.Message);
        }
        catch (EventAcceptanceException exception)
        {
            return Refused("event_identity_rule", exception.Message);
        }

        if (query.Schema is JsonElement declaredSchema)
        {
            try
            {
                ConstrainedJsonSchemaValidator.Validate(declaredSchema, "schema");
            }
            catch (ConnectorManifestValidationException exception)
            {
                return Refused("schema", exception.Message);
            }
        }

        // No mapping is a real Source configuration: ingestion then takes the input itself as the
        // Event, so the preview checks exactly that rather than refusing to answer.
        TransformSpec? mapping = null;
        if (query.Mapping is JsonElement declaredMapping)
        {
            string? mappingError = MappingConfigValidator.Validate(
                declaredMapping, evaluator, "mapping", out mapping);
            if (mappingError is not null || mapping is null)
                return Refused("mapping", mappingError ?? "The mapping is not valid.");
        }

        string inputJson = query.SampleInput.ValueKind == JsonValueKind.Undefined
            ? "{}"
            : query.SampleInput.GetRawText();

        if (query.Schema is JsonElement schemaForInstance)
        {
            try
            {
                ConfigurationSchemaEvaluator.Validate(
                    query.SampleInput, schemaForInstance, "sample_input");
            }
            catch (ConfigurationValidationException exception)
            {
                return Refused("schema", exception.Message);
            }
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            string outputJson = mapping is { } spec
                ? evaluator.Evaluate(spec, inputJson, query.SampleContext)
                : inputJson;
            SourceContractOutput output = SourceMappingOutputValidator.Validate(outputJson);
            // Intake refuses an Event type the Source does not declare, so the preview does too.
            if (query.EventTypes is { } declared
                && !declared.Contains(output.EventType, StringComparer.OrdinalIgnoreCase))
            {
                return Refused(
                    "event_types",
                    $"Event type '{output.EventType}' is not declared by this Source. Add it to the Source's Event types.");
            }
            // Ingestion falls back to the identity the output carries when the rule yields none. A
            // broker message id yields none here only because a sample cannot carry one, so it does
            // not fall back.
            if (query.EventIdentityRule?.Kind != "message_id")
                sourceEventId ??= output.SourceEventId;
            return Task.FromResult(new PreviewSourceContractResult(null, null, outputJson, sourceEventId));
        }
        catch (TransformEvaluationException exception)
        {
            // With no mapping the output is the input itself, so it is the input that is refused.
            return Refused(mapping is null ? "sample_input" : "mapping", exception.Message);
        }
    }

    private static Task<PreviewSourceContractResult> Refused(string refusedBy, string error) =>
        Task.FromResult(PreviewSourceContractResult.Refused(refusedBy, error));

    // The rule the Source would really use, read by the extractor Ingestion uses, so a sample that
    // would be refused for want of an identity is refused here too. A broker message id is supplied
    // by the transport and cannot be resolved from a sample, so it is reported as unresolved rather
    // than as missing.
    private static string? ResolveIdentity(PreviewSourceContractQuery query, SourceType sourceType)
    {
        if (query.EventIdentityRule is not { } rule)
            return null;

        SourceAuthoringValidator.ValidateEventIdentityRule(sourceType, rule);
        if (rule.Kind == "message_id")
            return null;

        return sourceType == SourceType.Webhook
            ? SourceEventIdentityExtractor.ExtractWebhook(rule, SampleHeaders(query.SampleContext), query.SampleInput)
            : SourceEventIdentityExtractor.ExtractBroker(rule, null, query.SampleInput);
    }

    private static IReadOnlyDictionary<string, string> SampleHeaders(JsonElement? sampleContext)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (sampleContext is not { } context
            || context.ValueKind != JsonValueKind.Object
            || !context.TryGetProperty("headers", out JsonElement declared)
            || declared.ValueKind != JsonValueKind.Object)
        {
            return headers;
        }

        foreach (JsonProperty header in declared.EnumerateObject())
        {
            if (header.Value.ValueKind == JsonValueKind.String)
                headers[header.Name] = header.Value.GetString()!;
        }
        return headers;
    }
}
