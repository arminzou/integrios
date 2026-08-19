using Integrios.Application.ApiKeys;
using Integrios.Application.Common.Pagination;
using Integrios.Domain.Common;
using Integrios.Domain.Tenants;
using Integrios.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Integrios.Infrastructure.ApiKeys;

internal sealed class ApiKeyRepository(IntegriosDbContext context) : IApiKeyRepository
{
    public async Task<ApiKey> CreateAsync(ApiKey apiKey, CancellationToken cancellationToken)
    {
        context.ApiKeys.Add(apiKey);
        await context.SaveChangesAsync(cancellationToken);
        return apiKey;
    }

    public Task<ApiKey?> GetByIdAsync(Guid tenantId, Guid id, CancellationToken cancellationToken) =>
        context.ApiKeys.AsNoTracking().SingleOrDefaultAsync(
            apiKey => apiKey.TenantId == tenantId && apiKey.Id == id,
            cancellationToken);

    public async Task<(IReadOnlyList<ApiKey> Items, string? NextCursor)> ListByTenantAsync(
        Guid tenantId,
        string? afterCursor,
        int limit,
        CancellationToken cancellationToken)
    {
        DateTimeOffset cursorCreatedAt = default;
        Guid cursorId = default;
        bool hasCursor = afterCursor is not null
            && PageCursor.TryDecode(afterCursor, out cursorCreatedAt, out cursorId);

        IQueryable<ApiKey> query = context.ApiKeys.AsNoTracking().Where(apiKey => apiKey.TenantId == tenantId);
        if (hasCursor)
        {
            query = query.Where(apiKey =>
                apiKey.CreatedAt > cursorCreatedAt
                || (apiKey.CreatedAt == cursorCreatedAt && apiKey.Id.CompareTo(cursorId) > 0));
        }

        List<ApiKey> items = await query
            .OrderBy(apiKey => apiKey.CreatedAt)
            .ThenBy(apiKey => apiKey.Id)
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
        await context.ApiKeys
            .Where(apiKey =>
                apiKey.TenantId == tenantId
                && apiKey.Id == id
                && apiKey.Status == OperationalStatus.Active)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(apiKey => apiKey.Status, OperationalStatus.Disabled)
                    .SetProperty(apiKey => apiKey.RevokedAt, DateTimeOffset.UtcNow),
                cancellationToken) > 0;
}
