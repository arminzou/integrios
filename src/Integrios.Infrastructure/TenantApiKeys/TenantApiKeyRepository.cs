using Integrios.Application.TenantApiKeys;
using Integrios.Application.Common.Pagination;
using Integrios.Domain.Entities;
using Integrios.Domain.Enums;
using Integrios.Domain.ValueObjects;
using Integrios.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Integrios.Infrastructure.TenantApiKeys;

internal sealed class TenantApiKeyRepository(IntegriosDbContext context) : ITenantApiKeyRepository
{
    public async Task<TenantApiKey> CreateAsync(TenantApiKey tenantApiKey, CancellationToken cancellationToken)
    {
        context.TenantApiKeys.Add(tenantApiKey);
        await context.SaveChangesAsync(cancellationToken);
        return tenantApiKey;
    }

    public Task<TenantApiKey?> GetByIdAsync(Guid tenantId, Guid id, CancellationToken cancellationToken) =>
        context.TenantApiKeys.AsNoTracking().SingleOrDefaultAsync(
            tenantApiKey => tenantApiKey.TenantId == tenantId && tenantApiKey.Id == id,
            cancellationToken);

    public async Task<(IReadOnlyList<TenantApiKey> Items, string? NextCursor)> ListByTenantAsync(
        Guid tenantId,
        string? afterCursor,
        int limit,
        CancellationToken cancellationToken)
    {
        DateTimeOffset cursorCreatedAt = default;
        Guid cursorId = default;
        bool hasCursor = afterCursor is not null
            && PageCursor.TryDecode(afterCursor, out cursorCreatedAt, out cursorId);

        IQueryable<TenantApiKey> query = context.TenantApiKeys.AsNoTracking().Where(tenantApiKey => tenantApiKey.TenantId == tenantId);
        if (hasCursor)
        {
            query = query.Where(tenantApiKey =>
                tenantApiKey.CreatedAt > cursorCreatedAt
                || (tenantApiKey.CreatedAt == cursorCreatedAt && tenantApiKey.Id.CompareTo(cursorId) > 0));
        }

        List<TenantApiKey> items = await query
            .OrderBy(tenantApiKey => tenantApiKey.CreatedAt)
            .ThenBy(tenantApiKey => tenantApiKey.Id)
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

    public async Task<bool> RevokeAsync(Guid tenantId, Guid id, CancellationToken cancellationToken) =>
        await context.TenantApiKeys
            .Where(tenantApiKey =>
                tenantApiKey.TenantId == tenantId
                && tenantApiKey.Id == id
                && tenantApiKey.Status == OperationalStatus.Active)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(tenantApiKey => tenantApiKey.Status, OperationalStatus.Disabled)
                    .SetProperty(tenantApiKey => tenantApiKey.RevokedAt, DateTimeOffset.UtcNow),
                cancellationToken) > 0;
}
