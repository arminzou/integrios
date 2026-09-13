using Integrios.Application.Delivery;
using Integrios.Application.Secrets;
using Integrios.Domain.Entities;
using Integrios.Domain.Enums;
using Integrios.Domain.ValueObjects;
using Integrios.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Integrios.Infrastructure.Secrets;

internal sealed class SecretValidationReader(IntegriosDbContext context) : ISecretValidationReader
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

    public Task<Destination?> FindDestinationAsync(
        Guid tenantId,
        Guid destinationId,
        CancellationToken cancellationToken) =>
        context.Destinations.AsNoTracking().SingleOrDefaultAsync(
            destination => destination.TenantId == tenantId && destination.Id == destinationId,
            cancellationToken);

    public async Task<IReadOnlyList<Destination>> ListActiveDestinationsAsync(
        Guid tenantId,
        CancellationToken cancellationToken) =>
        await context.Destinations.AsNoTracking()
            .Where(destination =>
                destination.TenantId == tenantId
                && destination.Status == OperationalStatus.Active)
            .OrderBy(destination => destination.CreatedAt)
            .ThenBy(destination => destination.Id)
            .ToListAsync(cancellationToken);
}
