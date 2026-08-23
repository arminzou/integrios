using System.Text.Json;

namespace Integrios.Application.Outbox;

public static class SubscriptionRoutingEvaluator
{
    public static IReadOnlyList<SubscriptionFanoutTarget> SelectTargets(
        string eventType,
        IReadOnlyList<SubscriptionRoutingCandidate> candidates)
    {
        return candidates
            .Where(candidate => Matches(candidate.MatchRulesJson, eventType))
            .OrderBy(candidate => candidate.OrderIndex)
            .ThenBy(candidate => candidate.SubscriptionId)
            .Select(candidate => new SubscriptionFanoutTarget(
                candidate.SubscriptionId,
                candidate.DestinationConnectionId,
                candidate.TransformConfigJson,
                candidate.ConnectorKey,
                candidate.HttpExecutionSnapshotJson))
            .ToList();
    }

    private static bool Matches(string? matchRulesJson, string eventType)
    {
        if (string.IsNullOrWhiteSpace(matchRulesJson))
            return false;

        try
        {
            using var document = JsonDocument.Parse(matchRulesJson);
            var rules = document.RootElement;
            if (rules.ValueKind != JsonValueKind.Object)
                return false;

            if (rules.TryGetProperty("event_type", out var currentRule) && currentRule.ValueKind == JsonValueKind.String)
                return string.Equals(currentRule.GetString(), eventType, StringComparison.OrdinalIgnoreCase);

            if (!rules.TryGetProperty("event_types", out var legacyRules) || legacyRules.ValueKind != JsonValueKind.Array)
                return false;

            return legacyRules.EnumerateArray().Any(rule =>
                rule.ValueKind == JsonValueKind.String &&
                string.Equals(rule.GetString(), eventType, StringComparison.OrdinalIgnoreCase));
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

public sealed record SubscriptionRoutingCandidate(
    Guid SubscriptionId,
    Guid DestinationConnectionId,
    int OrderIndex,
    string? MatchRulesJson,
    string? TransformConfigJson,
    string ConnectorKey,
    string HttpExecutionSnapshotJson);

public sealed record SubscriptionFanoutTarget(
    Guid SubscriptionId,
    Guid DestinationConnectionId,
    string? TransformConfigJson,
    string ConnectorKey,
    string HttpExecutionSnapshotJson);
