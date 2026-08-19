using Integrios.Application.Secrets;
using Integrios.Domain.Common;
using Integrios.Domain.Connections;
using Integrios.Domain.Tenants;
using Integrios.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Integrios.Infrastructure.Secrets;

internal sealed class SecretValidationCatalog(IntegriosDbContext context) : ISecretValidationCatalog
{
    public Task<Tenant?> FindTenantBySlugAsync(
        string slug,
        CancellationToken cancellationToken) =>
        context.Tenants.AsNoTracking().SingleOrDefaultAsync(
            tenant => tenant.Slug == slug,
            cancellationToken);

    public async Task<IReadOnlyList<Tenant>> ListActiveTenantsAsync(
        CancellationToken cancellationToken) =>
        await context.Tenants.AsNoTracking()
            .Where(tenant => tenant.Status == OperationalStatus.Active)
            .OrderBy(tenant => tenant.CreatedAt)
            .ThenBy(tenant => tenant.Id)
            .ToListAsync(cancellationToken);

    public Task<Connection?> FindConnectionAsync(
        Guid tenantId,
        Guid connectionId,
        CancellationToken cancellationToken) =>
        context.Connections.AsNoTracking().SingleOrDefaultAsync(
            connection => connection.TenantId == tenantId && connection.Id == connectionId,
            cancellationToken);

    public async Task<IReadOnlyList<Connection>> ListActiveConnectionsAsync(
        Guid tenantId,
        CancellationToken cancellationToken) =>
        await context.Connections.AsNoTracking()
            .Where(connection =>
                connection.TenantId == tenantId
                && connection.Status == OperationalStatus.Active)
            .OrderBy(connection => connection.CreatedAt)
            .ThenBy(connection => connection.Id)
            .ToListAsync(cancellationToken);
}
