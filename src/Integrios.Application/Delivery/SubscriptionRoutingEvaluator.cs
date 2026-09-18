namespace Integrios.Application.Delivery;

public static class SubscriptionRoutingEvaluator
{
    public static IReadOnlyList<SubscriptionFanoutTarget> SelectTargets(
        string eventType,
        IReadOnlyList<SubscriptionRoutingCandidate> candidates)
    {
        return candidates
            .Where(candidate => candidate.EventTypes.Contains(eventType, StringComparer.OrdinalIgnoreCase))
            .OrderBy(candidate => candidate.OrderIndex)
            .ThenBy(candidate => candidate.SubscriptionId)
            .Select(candidate => new SubscriptionFanoutTarget(
                candidate.SubscriptionId,
                candidate.DestinationId,
                candidate.MappingConfigJson,
                candidate.ConnectorKey,
                candidate.HttpExecutionSnapshotJson))
            .ToList();
    }
}

public sealed record SubscriptionRoutingCandidate(
    Guid SubscriptionId,
    Guid DestinationId,
    int OrderIndex,
    IReadOnlyList<string> EventTypes,
    string? MappingConfigJson,
    string ConnectorKey,
    string HttpExecutionSnapshotJson);

public sealed record SubscriptionFanoutTarget(
    Guid SubscriptionId,
    Guid DestinationId,
    string? MappingConfigJson,
    string ConnectorKey,
    string HttpExecutionSnapshotJson);
