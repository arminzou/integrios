namespace Integrios.Application.Abstractions;

public interface ISubscriptionRepository
{
    Task<Guid?> FindTopicIdAsync(Guid tenantId, string eventType, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SubscriptionTarget>> GetActiveSubscriptionsAsync(Guid topicId, CancellationToken cancellationToken = default);
}

public record SubscriptionTarget(Guid Id, string Name, string[] MatchEventTypes, Guid DestinationConnectionId, string DestinationUrl);
