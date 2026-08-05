using Integrios.Domain.Tenants;

namespace Integrios.Application.ApiKeys;

public interface IApiKeyRepository
{
    Task<ApiKey> CreateAsync(ApiKey apiKey, CancellationToken cancellationToken);
    Task<ApiKey?> GetByIdAsync(Guid tenantId, Guid id, CancellationToken cancellationToken);
    Task<(IReadOnlyList<ApiKey> Items, string? NextCursor)> ListByTenantAsync(Guid tenantId, string? afterCursor, int limit, CancellationToken cancellationToken);
    Task<bool> RevokeAsync(Guid tenantId, Guid id, CancellationToken cancellationToken);
}
