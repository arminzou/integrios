using Integrios.Domain.Entities;
using Integrios.Domain.Enums;

namespace Integrios.Application.Authoring.Topics;

public interface ITopicRepository
{
    Task<Topic> CreateAsync(Guid tenantId, string name, string? description, CancellationToken ct);
    Task<Topic?> GetByIdAsync(Guid tenantId, Guid id, CancellationToken ct);
    Task<int> CountSubscriptionsAsync(Guid tenantId, Guid topicId, CancellationToken ct);
    Task<(IReadOnlyList<TopicListRow> Items, string? NextCursor)> ListByTenantAsync(Guid tenantId, TopicListFilter filter, string? afterCursor, int limit, CancellationToken ct);
    Task<Topic?> UpdateAsync(
        Guid tenantId,
        Guid id,
        string? name,
        string? description,
        CancellationToken ct);
    Task<bool> DeactivateAsync(Guid tenantId, Guid id, CancellationToken ct);
}
