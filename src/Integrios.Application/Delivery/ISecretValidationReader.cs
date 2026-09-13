using Integrios.Application.Secrets;
using Integrios.Domain.Entities;
using Integrios.Domain.ValueObjects;

namespace Integrios.Application.Delivery;

public interface ISecretValidationReader
{
    Task<Tenant?> FindTenantBySlugAsync(
        string slug,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<Tenant>> ListActiveTenantsAsync(
        CancellationToken cancellationToken);

    Task<Destination?> FindDestinationAsync(
        Guid tenantId,
        Guid destinationId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<Destination>> ListActiveDestinationsAsync(
        Guid tenantId,
        CancellationToken cancellationToken);
}
