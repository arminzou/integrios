using Integrios.Domain.Connections;
using Integrios.Domain.Tenants;

namespace Integrios.Application.Secrets;

public interface ISecretValidationCatalog
{
    Task<Tenant?> FindTenantBySlugAsync(
        string slug,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<Tenant>> ListActiveTenantsAsync(
        CancellationToken cancellationToken);

    Task<Connection?> FindConnectionAsync(
        Guid tenantId,
        Guid connectionId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<Connection>> ListActiveConnectionsAsync(
        Guid tenantId,
        CancellationToken cancellationToken);
}
