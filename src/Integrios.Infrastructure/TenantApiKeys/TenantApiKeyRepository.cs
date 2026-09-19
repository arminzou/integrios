using Integrios.Application.Authoring.TenantApiKeys;
using Integrios.Application.Common.Exceptions;
using Integrios.Domain.Entities;
using Integrios.Domain.ValueObjects;
using Integrios.Infrastructure.Common.Pagination;
using Integrios.Infrastructure.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Integrios.Infrastructure.TenantApiKeys;

internal sealed class TenantApiKeyRepository(IntegriosDbContext context, IDataProtectionProvider dataProtectionProvider) : ITenantApiKeyRepository
{
    public async Task<TenantApiKey> CreateAsync(TenantApiKey tenantApiKey, CancellationToken cancellationToken)
    {
        context.TenantApiKeys.Add(tenantApiKey);
        await context.SaveChangesAsync(cancellationToken);
        return tenantApiKey;
    }

    public Task<TenantApiKey?> GetByIdAsync(Guid tenantId, Guid id, CancellationToken cancellationToken) =>
        context.TenantApiKeys.AsNoTracking().SingleOrDefaultAsync(
            tenantApiKey => tenantApiKey.TenantId == tenantId && tenantApiKey.Id == id && tenantApiKey.RevokedAt == null,
            cancellationToken);

    public async Task<(IReadOnlyList<TenantApiKey> Items, string? NextCursor)> ListByTenantAsync(
        Guid tenantId,
        TenantApiKeyListState? state,
        DateTimeOffset now,
        string? afterCursor,
        int limit,
        CancellationToken cancellationToken)
    {
        DateTimeOffset cursorCreatedAt = default;
        Guid cursorId = default;
        string cursorScope = $"tenant-api-keys:{tenantId:N}:{state?.ToString() ?? "all"}";
        bool hasCursor = afterCursor is not null;
        if (hasCursor && !PageCursor.TryDecode(dataProtectionProvider, afterCursor!, cursorScope, out cursorCreatedAt, out cursorId))
            throw new InvalidCursorException();

        IQueryable<TenantApiKey> query = context.TenantApiKeys.AsNoTracking()
            .Where(tenantApiKey => tenantApiKey.TenantId == tenantId && tenantApiKey.RevokedAt == null);
        query = state switch
        {
            TenantApiKeyListState.Active => query.Where(tenantApiKey => tenantApiKey.ExpiresAt == null || tenantApiKey.ExpiresAt > now),
            TenantApiKeyListState.Expired => query.Where(tenantApiKey => tenantApiKey.ExpiresAt != null && tenantApiKey.ExpiresAt <= now),
            _ => query,
        };
        if (hasCursor)
        {
            query = query.Where(tenantApiKey =>
                tenantApiKey.CreatedAt < cursorCreatedAt
                || (tenantApiKey.CreatedAt == cursorCreatedAt && tenantApiKey.Id.CompareTo(cursorId) < 0));
        }

        List<TenantApiKey> items = await query
            .OrderByDescending(tenantApiKey => tenantApiKey.CreatedAt)
            .ThenByDescending(tenantApiKey => tenantApiKey.Id)
            .Take(limit + 1)
            .ToListAsync(cancellationToken);

        string? nextCursor = null;
        if (items.Count > limit)
        {
            items.RemoveAt(items.Count - 1);
            nextCursor = PageCursor.Encode(dataProtectionProvider, cursorScope, items[^1].CreatedAt, items[^1].Id, DateTimeOffset.UtcNow);
        }

        return (items, nextCursor);
    }

    public async Task<bool> RevokeAsync(Guid tenantId, Guid id, CancellationToken cancellationToken) =>
        await context.TenantApiKeys
            .Where(tenantApiKey =>
                tenantApiKey.TenantId == tenantId
                && tenantApiKey.Id == id
                && tenantApiKey.RevokedAt == null)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(tenantApiKey => tenantApiKey.RevokedAt, DateTimeOffset.UtcNow),
                cancellationToken) > 0;
}
