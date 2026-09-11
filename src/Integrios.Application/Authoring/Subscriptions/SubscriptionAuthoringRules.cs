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
        HttpSuccessRule? httpSuccess,
        ITransformEvaluator transformEvaluator)
    {
        if (!HasValidMatchRulesShape(matchRules))
            throw new SubscriptionValidationException(InvalidMatchRulesMessage);

        HttpDeliveryConfigurationRules.Validate(httpDelivery);
        ValidateHttpSuccess(httpSuccess);

        if (transformConfig is null || transformConfig.Value.ValueKind == JsonValueKind.Null)
            return;

        string? error = MappingConfigValidator.Validate(
            transformConfig.Value,
            transformEvaluator,
            "transform",
            out _);
        if (error is not null)
            throw new SubscriptionValidationException(error, "mapping");
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

    private static void ValidateHttpSuccess(HttpSuccessRule? httpSuccess)
    {
        if (httpSuccess is null)
            return;

        if (httpSuccess.Evaluator != "json_boolean")
            throw new SubscriptionValidationException("http_success.evaluator must be json_boolean.");

        if (string.IsNullOrWhiteSpace(httpSuccess.Field))
            throw new SubscriptionValidationException("http_success.field is required for json_boolean.");

        if (httpSuccess.Expected is null)
            throw new SubscriptionValidationException("http_success.expected must be a boolean for json_boolean.");

        if (httpSuccess.DiagnosticField is { } diagnosticField && string.IsNullOrWhiteSpace(diagnosticField))
        {
            throw new SubscriptionValidationException(
                "http_success.diagnostic_field must be a non-empty top-level field name.");
        }

        if (httpSuccess.MaxBodyBytes is { } maxBodyBytes and (< 1 or > 1_048_576))
        {
            throw new SubscriptionValidationException(
                "http_success.max_body_bytes must be an integer from 1 through 1048576.");
        }
    }
}
