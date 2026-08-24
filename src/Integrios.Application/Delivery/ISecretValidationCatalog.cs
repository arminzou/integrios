using Integrios.Application.Secrets;
using Integrios.Domain.Entities;
using Integrios.Domain.ValueObjects;

namespace Integrios.Application.Delivery;

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
