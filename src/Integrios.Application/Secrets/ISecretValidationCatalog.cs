using Integrios.Domain.Integrations;
using Integrios.Domain.Tenants;

namespace Integrios.Application.Secrets;

public interface ISecretValidationCatalog
{
    Task<Tenant?> FindTenantBySlugAsync(
        string slug,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Tenant>> ListActiveTenantsAsync(
        CancellationToken cancellationToken = default);

    Task<Connection?> FindConnectionAsync(
        Guid tenantId,
        Guid connectionId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Connection>> ListActiveConnectionsAsync(
        Guid tenantId,
        CancellationToken cancellationToken = default);
}
