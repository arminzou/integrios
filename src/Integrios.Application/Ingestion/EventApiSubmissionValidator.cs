using System.Text.Json;
using Integrios.Application.Transforms;

namespace Integrios.Application.Ingestion;

internal static class EventApiSubmissionValidator
{
    public static SourceContractOutput Validate(JsonElement input)
    {
        try
        {
            SourceContractOutput output = SourceMappingOutputValidator.Validate(input.GetRawText());
            if (output.SourceEventId is not null && string.IsNullOrWhiteSpace(output.SourceEventId))
                throw new EventAcceptanceException("Event API 'source_event_id' must be a non-empty string when present.");
            return output;
        }
        catch (TransformEvaluationException exception)
        {
            throw new EventAcceptanceException(exception.Message);
        }
    }
}
