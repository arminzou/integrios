using System.Text.Json;
using Integrios.Domain.Topics;

namespace Integrios.Application.Abstractions;

public interface ISubscriptionRepository
{
    Task<Subscription> CreateAsync(
        Guid tenantId,
        Guid topicId,
        string name,
        JsonElement matchRules,
        Guid destinationConnectionId,
        bool dlqEnabled,
        int orderIndex,
        string? description,
        CancellationToken cancellationToken = default);

    Task<Subscription?> GetByIdAsync(Guid tenantId, Guid topicId, Guid id, CancellationToken cancellationToken = default);

    Task<(IReadOnlyList<Subscription> Items, string? NextCursor)> ListByTopicAsync(
        Guid tenantId,
        Guid topicId,
        string? afterCursor,
        int limit,
        CancellationToken cancellationToken = default);

    Task<Subscription?> UpdateAsync(
        Guid tenantId,
        Guid topicId,
        Guid id,
        string name,
        JsonElement matchRules,
        Guid destinationConnectionId,
        bool dlqEnabled,
        int orderIndex,
        string? description,
        CancellationToken cancellationToken = default);

    Task<bool> DeactivateAsync(Guid tenantId, Guid topicId, Guid id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SubscriptionTarget>> GetActiveSubscriptionsAsync(Guid topicId, CancellationToken cancellationToken = default);
}

public record SubscriptionTarget(Guid Id, string Name, string[] MatchEventTypes, Guid DestinationConnectionId, string DestinationUrl);
