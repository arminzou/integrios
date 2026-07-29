using Integrios.Domain.Tenants;

namespace Integrios.Application.Abstractions;

public interface IApiKeyRepository
{
    Task<ApiKey> CreateAsync(ApiKey apiKey, CancellationToken cancellationToken = default);
    Task<ApiKey?> GetByIdAsync(Guid tenantId, Guid id, CancellationToken cancellationToken = default);
    Task<(IReadOnlyList<ApiKey> Items, string? NextCursor)> ListByTenantAsync(Guid tenantId, string? afterCursor, int limit, CancellationToken cancellationToken = default);
    Task<bool> RevokeAsync(Guid tenantId, Guid id, CancellationToken cancellationToken = default);
}
