using System.Text.Json;
using Integrios.Application.Common.Pagination;
using Integrios.Application.Sources;
using Integrios.Domain.Entities;
using Integrios.Domain.Enums;
using Integrios.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Integrios.Infrastructure.Sources;

internal sealed class SourceRepository(IntegriosDbContext context) : ISourceRepository
{
    public async Task<Source> CreateAsync(Source source, CancellationToken cancellationToken)
    {
        context.Sources.Add(source);
        await context.SaveChangesAsync(cancellationToken);
        return source;
    }

    public Task<Source?> GetByIdAsync(Guid tenantId, Guid id, CancellationToken cancellationToken) =>
        context.Sources.AsNoTracking().SingleOrDefaultAsync(source => source.TenantId == tenantId && source.Id == id, cancellationToken);

    public async Task<(IReadOnlyList<Source> Items, string? NextCursor)> ListByTenantAsync(
        Guid tenantId, string? afterCursor, int limit, CancellationToken cancellationToken)
    {
        DateTimeOffset cursorTime = default;
        Guid cursorId = default;
        bool hasCursor = afterCursor is not null && PageCursor.TryDecode(afterCursor, out cursorTime, out cursorId);
        IQueryable<Source> query = context.Sources.AsNoTracking().Where(source => source.TenantId == tenantId);
        if (hasCursor)
            query = query.Where(source => source.CreatedAt > cursorTime || (source.CreatedAt == cursorTime && source.Id.CompareTo(cursorId) > 0));
        List<Source> items = await query.OrderBy(source => source.CreatedAt).ThenBy(source => source.Id).Take(limit + 1).ToListAsync(cancellationToken);
        bool hasMore = items.Count > limit;
        if (hasMore)
            items.RemoveAt(items.Count - 1);
        return (items, hasMore ? PageCursor.Encode(items[^1].CreatedAt, items[^1].Id) : null);
    }

    public async Task<Source?> UpdateAsync(Guid tenantId, Guid id, JsonElement configuration, CancellationToken cancellationToken)
    {
        int affected = await context.Sources.Where(source => source.TenantId == tenantId && source.Id == id && source.Status == SourceStatus.Active)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(source => source.Configuration, configuration)
                .SetProperty(source => source.UpdatedAt, DateTimeOffset.UtcNow), cancellationToken);
        return affected == 0 ? null : await GetByIdAsync(tenantId, id, cancellationToken);
    }

    public Task<bool> RevokeAsync(Guid tenantId, Guid id, CancellationToken cancellationToken) =>
        context.Sources.Where(source => source.TenantId == tenantId && source.Id == id && source.Status == SourceStatus.Active)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(source => source.Status, SourceStatus.Revoked)
                .SetProperty(source => source.RevokedAt, DateTimeOffset.UtcNow)
                .SetProperty(source => source.UpdatedAt, DateTimeOffset.UtcNow), cancellationToken)
            .ContinueWith(task => task.Result > 0, cancellationToken);
}
