using Integrios.Domain.Topics;

namespace Integrios.Application.Topics;

public interface ITopicRepository
{
    Task<Topic> CreateAsync(Guid tenantId, string name, string? description, IReadOnlyList<Guid> sourceConnectionIds, CancellationToken ct);
    Task<Topic?> GetByIdAsync(Guid tenantId, Guid id, CancellationToken ct);
    Task<(IReadOnlyList<Topic> Items, string? NextCursor)> ListByTenantAsync(Guid tenantId, string? afterCursor, int limit, CancellationToken ct);
    Task<Topic?> UpdateAsync(
        Guid tenantId,
        Guid id,
        string? name,
        string? description,
        IReadOnlyList<Guid>? sourceConnectionIds,
        CancellationToken ct);
    Task<bool> DeactivateAsync(Guid tenantId, Guid id, CancellationToken ct);
}
