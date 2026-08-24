using System.Text.Json;
using Integrios.Application.Transforms;
using Integrios.Domain.Entities;
using Integrios.Domain.ValueObjects;

namespace Integrios.Application.Authoring.Subscriptions;

internal static class SubscriptionAuthoringRules
{
    private const string InvalidMatchRulesMessage =
        "matchRules must be an object with exactly one non-empty string property: event_type";

    public static void Validate(
        JsonElement matchRules,
        JsonElement? transformConfig,
        HttpDeliveryConfiguration httpDelivery,
        ITransformEvaluator transformEvaluator)
    {
        if (!HasValidMatchRulesShape(matchRules))
            throw new SubscriptionValidationException(InvalidMatchRulesMessage);

        HttpDeliveryConfigurationRules.Validate(httpDelivery);

        if (transformConfig is null || transformConfig.Value.ValueKind == JsonValueKind.Null)
            return;

        string? error = MappingConfigValidator.Validate(
            transformConfig.Value,
            transformEvaluator,
            "transform",
            out _);
        if (error is not null)
            throw new SubscriptionValidationException(error);
    }

    private static bool HasValidMatchRulesShape(JsonElement matchRules)
    {
        if (matchRules.ValueKind != JsonValueKind.Object)
            return false;

        var enumerator = matchRules.EnumerateObject();
        if (!enumerator.MoveNext())
            return false;

        JsonProperty property = enumerator.Current;
        return property.Name == "event_type"
            && !enumerator.MoveNext()
            && property.Value.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(property.Value.GetString());
    }
}
