using System.Text.Json;
using Integrios.Application.Connections;
using Integrios.Application.Transforms;
using MediatR;

namespace Integrios.Application.Connectors;

public sealed record PreviewSourceContractQuery(
    JsonElement? Schema,
    JsonElement Mapping,
    JsonElement SampleInput,
    JsonElement? SampleContext) : IRequest<PreviewSourceContractResult>;

public sealed record PreviewSourceContractResult(string? Error, string? OutputJson);

internal sealed class PreviewSourceContractQueryHandler(ITransformEvaluator evaluator)
    : IRequestHandler<PreviewSourceContractQuery, PreviewSourceContractResult>
{
    private static readonly HashSet<string> AllowedOutputFields =
        ["event_type", "source_event_id", "payload", "metadata"];

    public Task<PreviewSourceContractResult> Handle(
        PreviewSourceContractQuery query,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (query.Schema is JsonElement declaredSchema)
        {
            try
            {
                ConstrainedJsonSchemaValidator.Validate(declaredSchema, "schema");
            }
            catch (ConnectorManifestValidationException exception)
            {
                return Task.FromResult(new PreviewSourceContractResult(exception.Message, null));
            }
        }

        string? mappingError = MappingConfigValidator.Validate(
            query.Mapping, evaluator, "mapping", out TransformSpec? mapping);
        if (mappingError is not null || mapping is null)
            return Task.FromResult(new PreviewSourceContractResult(mappingError, null));

        string inputJson = query.SampleInput.ValueKind == JsonValueKind.Undefined
            ? "{}"
            : query.SampleInput.GetRawText();

        if (query.Schema is JsonElement schemaForInstance)
        {
            try
            {
                ConnectionConfigurationSchemaEvaluator.Validate(
                    query.SampleInput, schemaForInstance, "sample_input");
            }
            catch (ConnectionConfigurationValidationException exception)
            {
                return Task.FromResult(new PreviewSourceContractResult(exception.Message, null));
            }
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            string outputJson = evaluator.Evaluate(mapping, inputJson, query.SampleContext);
            ValidateOutput(outputJson);
            return Task.FromResult(new PreviewSourceContractResult(null, outputJson));
        }
        catch (TransformEvaluationException exception)
        {
            return Task.FromResult(new PreviewSourceContractResult(exception.Message, null));
        }
    }

    private static void ValidateOutput(string outputJson)
    {
        JsonElement output;
        try
        {
            output = JsonSerializer.Deserialize<JsonElement>(outputJson);
        }
        catch (JsonException exception)
        {
            throw new TransformEvaluationException($"Source mapping output must be valid JSON: {exception.Message}");
        }

        if (output.ValueKind != JsonValueKind.Object)
            throw new TransformEvaluationException("Source mapping output must be a JSON object.");

        foreach (JsonProperty property in output.EnumerateObject())
        {
            if (!AllowedOutputFields.Contains(property.Name))
                throw new TransformEvaluationException(
                    $"Source mapping output contains unsupported field '{property.Name}'.");
        }

        if (!output.TryGetProperty("event_type", out JsonElement eventType)
            || eventType.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(eventType.GetString()))
        {
            throw new TransformEvaluationException("Source mapping output must include a non-empty 'event_type' string.");
        }

        if (!output.TryGetProperty("payload", out _))
            throw new TransformEvaluationException("Source mapping output must include 'payload'.");

        if (output.TryGetProperty("source_event_id", out JsonElement sourceEventId)
            && sourceEventId.ValueKind != JsonValueKind.String)
        {
            throw new TransformEvaluationException("Source mapping output 'source_event_id' must be a string when present.");
        }

        if (output.TryGetProperty("metadata", out JsonElement metadata)
            && metadata.ValueKind != JsonValueKind.Object)
        {
            throw new TransformEvaluationException("Source mapping output 'metadata' must be an object when present.");
        }
    }
}
