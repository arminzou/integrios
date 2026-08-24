using System.Text.Json;
using Integrios.Application.Authoring.Connections;
using Integrios.Application.Transforms;
using MediatR;

namespace Integrios.Application.Authoring.Connectors;

public sealed record PreviewSourceContractQuery(
    JsonElement? Schema,
    JsonElement Mapping,
    JsonElement SampleInput,
    JsonElement? SampleContext) : IRequest<PreviewSourceContractResult>;

public sealed record PreviewSourceContractResult(string? Error, string? OutputJson);

internal sealed class PreviewSourceContractQueryHandler(ITransformEvaluator evaluator)
    : IRequestHandler<PreviewSourceContractQuery, PreviewSourceContractResult>
{
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
            SourceMappingOutputValidator.Validate(outputJson);
            return Task.FromResult(new PreviewSourceContractResult(null, outputJson));
        }
        catch (TransformEvaluationException exception)
        {
            return Task.FromResult(new PreviewSourceContractResult(exception.Message, null));
        }
    }
}
