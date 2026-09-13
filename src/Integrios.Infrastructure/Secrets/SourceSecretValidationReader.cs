using Integrios.Application.Ingestion;
using Integrios.Domain.Entities;
using Integrios.Domain.Enums;
using Integrios.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Integrios.Infrastructure.Secrets;

internal sealed class SourceSecretValidationReader(IntegriosDbContext context) : ISourceSecretValidationReader
{
    public Task<Tenant?> FindTenantBySlugAsync(string slug, CancellationToken cancellationToken) =>
        context.Tenants.AsNoTracking().SingleOrDefaultAsync(
            tenant => tenant.Slug == slug,
            cancellationToken);

    public async Task<IReadOnlyList<Tenant>> ListActiveTenantsAsync(CancellationToken cancellationToken) =>
        await context.Tenants.AsNoTracking()
            .Where(tenant => tenant.Status == OperationalStatus.Active)
            .OrderBy(tenant => tenant.CreatedAt)
            .ThenBy(tenant => tenant.Id)
            .ToListAsync(cancellationToken);

    public Task<Source?> FindSourceAsync(Guid tenantId, Guid sourceId, CancellationToken cancellationToken) =>
        context.Sources.AsNoTracking().SingleOrDefaultAsync(
            source => source.TenantId == tenantId && source.Id == sourceId,
            cancellationToken);

    // Revocation is permanent, so a revoked Source's references can never be needed again.
    public async Task<IReadOnlyList<Source>> ListActiveSourcesAsync(
        Guid tenantId,
        CancellationToken cancellationToken) =>
        await context.Sources.AsNoTracking()
            .Where(source => source.TenantId == tenantId && source.Status == SourceStatus.Active)
            .OrderBy(source => source.CreatedAt)
            .ThenBy(source => source.Id)
            .ToListAsync(cancellationToken);
}
