using Integrios.Domain.Entities;

namespace Integrios.Application.Ingestion;

/// Ingestion's own read side for pre-flighting Source credentials. It is separate from the Worker's
/// destination reader because the two processes deliberately cannot see each other's secret mounts:
/// Admin authors a Source but holds no resolver at all, so this question can only be answered here.
public interface ISourceSecretValidationReader
{
    Task<Tenant?> FindTenantBySlugAsync(string slug, CancellationToken cancellationToken);

    Task<IReadOnlyList<Tenant>> ListActiveTenantsAsync(CancellationToken cancellationToken);

    Task<Source?> FindSourceAsync(Guid tenantId, Guid sourceId, CancellationToken cancellationToken);

    Task<IReadOnlyList<Source>> ListActiveSourcesAsync(Guid tenantId, CancellationToken cancellationToken);
}
