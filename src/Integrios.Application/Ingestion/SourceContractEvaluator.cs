using System.Text.Json;
using Integrios.Application.Authoring.Connections;
using Integrios.Application.Transforms;

namespace Integrios.Application.Ingestion;

// Shared by every Ingestion path (Event API, webhook, queue): validate the raw input document
// against the Source contract's optional schema, then run its optional JSONata mapping (or, with
// no mapping declared, treat the raw input itself as the strictly bounded output) and validate the
// result. A rejection here is always a Source rejection (EventAcceptanceException -> 422), never an
// Event or Delivery failure.
internal static class SourceContractEvaluator
{
    public static SourceContractOutput Evaluate(
        ITransformEvaluator evaluator,
        JsonElement? schema,
        TransformSpec? mapping,
        JsonElement rawInput,
        JsonElement? context = null)
    {
        if (schema is JsonElement declaredSchema)
        {
            try
            {
                ConnectionConfigurationSchemaEvaluator.Validate(rawInput, declaredSchema, "input");
            }
            catch (ConnectionConfigurationValidationException exception)
            {
                throw new EventAcceptanceException(exception.Message);
            }
        }

        try
        {
            string outputJson = mapping is { } spec
                ? evaluator.Evaluate(spec, rawInput.GetRawText(), context)
                : rawInput.GetRawText();
            return SourceMappingOutputValidator.Validate(outputJson);
        }
        catch (TransformEvaluationException exception)
        {
            throw new EventAcceptanceException(exception.Message);
        }
    }
}
