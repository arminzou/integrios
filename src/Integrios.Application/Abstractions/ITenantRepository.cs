using Integrios.Domain.Tenants;

namespace Integrios.Application.Abstractions;

public interface ITenantRepository
{
    Task<Tenant> CreateAsync(Tenant tenant, CancellationToken cancellationToken = default);
    Task<Tenant?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<(IReadOnlyList<Tenant> Items, string? NextCursor)> ListAsync(
        string? afterCursor, int limit, CancellationToken cancellationToken = default);
    Task<Tenant?> UpdateAsync(Guid id, string name, string? description, string? environment, CancellationToken cancellationToken = default);
    Task<bool> DeactivateAsync(Guid id, CancellationToken cancellationToken = default);
}
