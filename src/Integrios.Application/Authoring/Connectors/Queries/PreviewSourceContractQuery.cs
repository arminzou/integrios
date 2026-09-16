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
    JsonElement Mapping,
    JsonElement SampleInput,
    JsonElement? SampleContext,
    SourceEventIdentityRule? EventIdentityRule) : IRequest<PreviewSourceContractResult>;

// SourceEventId is what the rule selects from this sample, so the preview shows the Event as
// Ingestion would accept it rather than the mapping output alone. Null where the rule reads
// something a sample cannot carry -- a broker message id -- or where an absent value is permitted.
public sealed record PreviewSourceContractResult(string? Error, string? OutputJson, string? SourceEventId);

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
            return Task.FromResult(new PreviewSourceContractResult(exception.Message, null, null));
        }
        catch (EventAcceptanceException exception)
        {
            return Task.FromResult(new PreviewSourceContractResult(exception.Message, null, null));
        }

        if (query.Schema is JsonElement declaredSchema)
        {
            try
            {
                ConstrainedJsonSchemaValidator.Validate(declaredSchema, "schema");
            }
            catch (ConnectorManifestValidationException exception)
            {
                return Task.FromResult(new PreviewSourceContractResult(exception.Message, null, null));
            }
        }

        string? mappingError = MappingConfigValidator.Validate(
            query.Mapping, evaluator, "mapping", out TransformSpec? mapping);
        if (mappingError is not null || mapping is null)
            return Task.FromResult(new PreviewSourceContractResult(mappingError, null, null));

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
                return Task.FromResult(new PreviewSourceContractResult(exception.Message, null, null));
            }
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            string outputJson = evaluator.Evaluate(mapping, inputJson, query.SampleContext);
            SourceMappingOutputValidator.Validate(outputJson);
            return Task.FromResult(new PreviewSourceContractResult(null, outputJson, sourceEventId));
        }
        catch (TransformEvaluationException exception)
        {
            return Task.FromResult(new PreviewSourceContractResult(exception.Message, null, null));
        }
    }

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
