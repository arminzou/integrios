using System.Text.Json;
using Integrios.Application.Common;
using Integrios.Application.Transforms;
using Integrios.Domain.Entities;
using Integrios.Domain.ValueObjects;

namespace Integrios.Application.Authoring.Subscriptions;

internal static class SubscriptionAuthoringRules
{
    public static void Validate(
        IReadOnlyList<string>? eventTypes,
        JsonElement? transformConfig,
        HttpDeliveryConfiguration httpDelivery,
        HttpSuccessRule? httpSuccess,
        ITransformEvaluator transformEvaluator)
    {
        ValidateEventTypes(eventTypes);

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

    private static void ValidateEventTypes(IReadOnlyList<string>? eventTypes)
    {
        if (eventTypes is null || eventTypes.Count == 0)
            throw new SubscriptionValidationException("Select at least one Event type to route.", "event_types");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string? eventType in eventTypes)
        {
            // A Subscription matching a value no Event can carry would wait for nothing, silently.
            if (EventTypeName.Problem(eventType) is { } problem)
                throw new SubscriptionValidationException($"Event type {problem}.", "event_types");
            if (!seen.Add(eventType!))
                throw new SubscriptionValidationException($"Event type '{eventType}' is selected more than once.", "event_types");
        }
    }

    // Only a type some Source on the Topic declares can ever arrive, so anything else is refused
    // rather than stored as a route no producer satisfies. Kept in the Topic's spelling.
    public static IReadOnlyList<string> SelectFromTopic(IReadOnlyList<string> eventTypes, IReadOnlyList<string> available)
    {
        var selected = new List<string>(eventTypes.Count);
        foreach (string eventType in eventTypes)
        {
            string? declared = available.FirstOrDefault(
                candidate => string.Equals(candidate, eventType, StringComparison.OrdinalIgnoreCase));
            selected.Add(declared ?? throw new SubscriptionValidationException(
                $"Event type '{eventType}' is not declared by any Source on this Topic.", "event_types"));
        }
        return selected;
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
