using System.Text.Json;
using Integrios.Domain.Topics;

namespace Integrios.Application.Subscriptions;

public interface ISubscriptionRepository
{
    Task<Subscription?> CreateAsync(
        Guid tenantId,
        Guid topicId,
        string name,
        JsonElement matchRules,
        Guid destinationConnectionId,
        JsonElement? transformConfig,
        int orderIndex,
        string? description,
        CancellationToken cancellationToken);

    Task<Subscription?> GetByIdAsync(Guid tenantId, Guid topicId, Guid id, CancellationToken cancellationToken);

    Task<(IReadOnlyList<Subscription> Items, string? NextCursor)> ListByTopicAsync(
        Guid tenantId,
        Guid topicId,
        string? afterCursor,
        int limit,
        CancellationToken cancellationToken);

    Task<Subscription?> UpdateAsync(
        Guid tenantId,
        Guid topicId,
        Guid id,
        string name,
        JsonElement matchRules,
        Guid destinationConnectionId,
        JsonElement? transformConfig,
        int orderIndex,
        string? description,
        CancellationToken cancellationToken);

    Task<bool> DeactivateAsync(Guid tenantId, Guid topicId, Guid id, CancellationToken cancellationToken);
}
