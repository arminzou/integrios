using Integrios.Application.Common.Exceptions;
using Integrios.Application.Common.Pagination;
using Integrios.Application.Authoring.Tenants;
using Integrios.Domain.Entities;
using Integrios.Domain.Enums;
using Integrios.Domain.ValueObjects;
using Integrios.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.SqlClient;
using Npgsql;

namespace Integrios.Infrastructure.Tenants;

internal sealed class TenantRepository(IntegriosDbContext context) : ITenantRepository
{
    public async Task<Tenant> CreateAsync(Tenant tenant, CancellationToken cancellationToken)
    {
        context.Tenants.Add(tenant);
        try
        {
            await context.SaveChangesAsync(cancellationToken);
            return tenant;
        }
        catch (DbUpdateException ex) when (
            ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation }
            || ex.InnerException is SqlException { Number: 2601 or 2627 })
        {
            throw new DuplicateResourceException("A tenant with that slug already exists.", ex);
        }
    }

    public Task<Tenant?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
        context.Tenants.AsNoTracking().SingleOrDefaultAsync(tenant => tenant.Id == id, cancellationToken);

    public async Task<(IReadOnlyList<Tenant> Items, string? NextCursor)> ListAsync(
        string? afterCursor,
        int limit,
        CancellationToken cancellationToken)
    {
        DateTimeOffset cursorCreatedAt = default;
        Guid cursorId = default;
        bool hasCursor = afterCursor is not null
            && PageCursor.TryDecode(afterCursor, out cursorCreatedAt, out cursorId);

        IQueryable<Tenant> query = context.Tenants.AsNoTracking();
        if (hasCursor)
        {
            query = query.Where(tenant =>
                tenant.CreatedAt > cursorCreatedAt
                || (tenant.CreatedAt == cursorCreatedAt && tenant.Id.CompareTo(cursorId) > 0));
        }

        List<Tenant> items = await query
            .OrderBy(tenant => tenant.CreatedAt)
            .ThenBy(tenant => tenant.Id)
            .Take(limit + 1)
            .ToListAsync(cancellationToken);

        string? nextCursor = null;
        if (items.Count > limit)
        {
            items.RemoveAt(items.Count - 1);
            nextCursor = PageCursor.Encode(items[^1].CreatedAt, items[^1].Id);
        }

        return (items, nextCursor);
    }

    public async Task<Tenant?> UpdateAsync(
        Guid id,
        string name,
        string? description,
        string? environment,
        CancellationToken cancellationToken)
    {
        DateTimeOffset updatedAt = DateTimeOffset.UtcNow;
        int affected = await context.Tenants
            .Where(tenant => tenant.Id == id)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(tenant => tenant.Name, name)
                    .SetProperty(tenant => tenant.Description, description)
                    .SetProperty(tenant => tenant.Environment, environment)
                    .SetProperty(tenant => tenant.UpdatedAt, updatedAt),
                cancellationToken);

        return affected == 0 ? null : await GetByIdAsync(id, cancellationToken);
    }

    public async Task<bool> DeactivateAsync(Guid id, CancellationToken cancellationToken) =>
        await context.Tenants
            .Where(tenant => tenant.Id == id && tenant.Status == OperationalStatus.Active)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(tenant => tenant.Status, OperationalStatus.Disabled)
                    .SetProperty(tenant => tenant.UpdatedAt, DateTimeOffset.UtcNow),
                cancellationToken) > 0;
}
