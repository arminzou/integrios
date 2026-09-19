using Integrios.Domain.Entities;
using Integrios.Domain.Enums;

namespace Integrios.Application.Authoring.Topics;

public interface ITopicRepository
{
    Task<Topic> CreateAsync(Guid tenantId, string key, string name, string? description, CancellationToken ct);
    Task<Topic?> GetByIdAsync(Guid tenantId, Guid id, CancellationToken ct);
    Task<int> CountSubscriptionsAsync(Guid tenantId, Guid topicId, CancellationToken ct);
    Task<int> CountSourcesAsync(Guid tenantId, Guid topicId, CancellationToken ct);
    Task<(IReadOnlyList<TopicListRow> Items, string? NextCursor)> ListByTenantAsync(Guid tenantId, TopicListFilter filter, string? afterCursor, int limit, CancellationToken ct);
    Task<Topic?> UpdateAsync(
        Guid tenantId,
        Guid id,
        string? name,
        string? description,
        CancellationToken ct);
    // Every Source that can still publish to these Topics, with what each declares.
    Task<IReadOnlyList<SourceDeclaration>> ListSourceDeclarationsAsync(
        Guid tenantId, IReadOnlyCollection<Guid> topicIds, CancellationToken ct);
    // Every Subscription on the Topic, whatever its status: a Disabled one can be enabled again.
    Task<IReadOnlyList<SubscriptionSelection>> ListSubscriptionSelectionsAsync(Guid tenantId, Guid topicId, CancellationToken ct);
    Task<bool> DeleteAsync(Guid tenantId, Guid id, CancellationToken ct);
}
