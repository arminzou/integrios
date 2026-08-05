using Integrios.Domain.Tenants;

namespace Integrios.Application.Tenants;

public interface ITenantRepository
{
    Task<Tenant> CreateAsync(Tenant tenant, CancellationToken cancellationToken);
    Task<Tenant?> GetByIdAsync(Guid id, CancellationToken cancellationToken);
    Task<(IReadOnlyList<Tenant> Items, string? NextCursor)> ListAsync(
        string? afterCursor, int limit, CancellationToken cancellationToken);
    Task<Tenant?> UpdateAsync(Guid id, string name, string? description, string? environment, CancellationToken cancellationToken);
    Task<bool> DeactivateAsync(Guid id, CancellationToken cancellationToken);
}
