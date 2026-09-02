using System.Text.Json;
using Integrios.Application.Common.Exceptions;
using Integrios.Application.Authoring.Sources;
using Integrios.Infrastructure.Common.Pagination;
using Integrios.Domain.Entities;
using Integrios.Domain.Enums;
using Integrios.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.DataProtection;

namespace Integrios.Infrastructure.Sources;

internal sealed class SourceRepository(IntegriosDbContext context, IDataProtectionProvider dataProtectionProvider) : ISourceRepository
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
        Guid tenantId, SourceStatus? status, SourceType? type, string? afterCursor, int limit, CancellationToken cancellationToken)
    {
        DateTimeOffset cursorTime = default;
        Guid cursorId = default;
        string cursorScope = $"sources:{tenantId:N}:{status?.ToString() ?? "all"}:{type?.ToString() ?? "all"}";
        bool hasCursor = afterCursor is not null;
        if (hasCursor && !PageCursor.TryDecode(dataProtectionProvider, afterCursor!, cursorScope, out cursorTime, out cursorId))
            throw new InvalidCursorException();
        IQueryable<Source> query = context.Sources.AsNoTracking().Where(source => source.TenantId == tenantId);
        if (status is not null)
            query = query.Where(source => source.Status == status);
        if (type is not null)
            query = query.Where(source => source.Type == type);
        if (hasCursor)
            query = query.Where(source => source.CreatedAt < cursorTime || (source.CreatedAt == cursorTime && source.Id.CompareTo(cursorId) < 0));
        List<Source> items = await query.OrderByDescending(source => source.CreatedAt).ThenByDescending(source => source.Id).Take(limit + 1).ToListAsync(cancellationToken);
        bool hasMore = items.Count > limit;
        if (hasMore)
            items.RemoveAt(items.Count - 1);
        return (items, hasMore ? PageCursor.Encode(dataProtectionProvider, cursorScope, items[^1].CreatedAt, items[^1].Id, DateTimeOffset.UtcNow) : null);
    }

    public async Task<Source?> UpdateAsync(Guid tenantId, Guid id, JsonElement configuration, CancellationToken cancellationToken)
    {
        int affected = await context.Sources.Where(source => source.TenantId == tenantId && source.Id == id && source.Status == SourceStatus.Active)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(source => source.Configuration, configuration)
                .SetProperty(source => source.UpdatedAt, DateTimeOffset.UtcNow), cancellationToken);
        return affected == 0 ? null : await GetByIdAsync(tenantId, id, cancellationToken);
    }

    public async Task<bool> RevokeAsync(Guid tenantId, Guid id, CancellationToken cancellationToken) =>
        await context.Sources.Where(source => source.TenantId == tenantId && source.Id == id && source.Status == SourceStatus.Active)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(source => source.Status, SourceStatus.Revoked)
                .SetProperty(source => source.RevokedAt, DateTimeOffset.UtcNow)
                .SetProperty(source => source.UpdatedAt, DateTimeOffset.UtcNow), cancellationToken) > 0;
}
