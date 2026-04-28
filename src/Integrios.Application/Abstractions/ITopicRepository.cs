using Integrios.Domain.Topics;

namespace Integrios.Application.Abstractions;

public interface ITopicRepository
{
    Task<Topic> CreateAsync(Guid tenantId, string name, string? description, IReadOnlyList<Guid> sourceConnectionIds, CancellationToken ct = default);
    Task<Topic?> GetByIdAsync(Guid tenantId, Guid id, CancellationToken ct = default);
    Task<(IReadOnlyList<Topic> Items, string? NextCursor)> ListByTenantAsync(Guid tenantId, string? afterCursor, int limit, CancellationToken ct = default);
    Task<Topic?> UpdateAsync(Guid tenantId, Guid id, string name, string? description, CancellationToken ct = default);
    Task<bool> DeactivateAsync(Guid tenantId, Guid id, CancellationToken ct = default);
    Task<bool> SetSourceConnectionsAsync(Guid tenantId, Guid id, IReadOnlyList<Guid> sourceConnectionIds, CancellationToken ct = default);
    Task<Guid?> FindByNameAsync(Guid tenantId, string name, CancellationToken ct = default);
}
