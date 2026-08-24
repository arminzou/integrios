using Integrios.Domain.Entities;
using Integrios.Domain.ValueObjects;

namespace Integrios.Application.Authoring.TenantApiKeys;

public interface ITenantApiKeyRepository
{
    Task<TenantApiKey> CreateAsync(TenantApiKey tenantApiKey, CancellationToken cancellationToken);
    Task<TenantApiKey?> GetByIdAsync(Guid tenantId, Guid id, CancellationToken cancellationToken);
    Task<(IReadOnlyList<TenantApiKey> Items, string? NextCursor)> ListByTenantAsync(Guid tenantId, string? afterCursor, int limit, CancellationToken cancellationToken);
    Task<bool> RevokeAsync(Guid tenantId, Guid id, CancellationToken cancellationToken);
}
