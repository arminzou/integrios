using Integrios.Application.Common.Pagination;
using Integrios.Application.Integrations;
using Integrios.Domain.Integrations;
using Integrios.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Integrios.Infrastructure.Integrations;

internal sealed class IntegrationCatalog(IntegriosDbContext context) : IIntegrationCatalog
{
    public Task<Integration?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
        context.Integrations.AsNoTracking().SingleOrDefaultAsync(
            integration => integration.Id == id,
            cancellationToken);

    public Task<Integration?> GetByVersionAsync(
        string key,
        int contractVersion,
        CancellationToken cancellationToken) =>
        context.Integrations.AsNoTracking().SingleOrDefaultAsync(
            integration => integration.Key == key && integration.ContractVersion == contractVersion,
            cancellationToken);

    public async Task<(IReadOnlyList<Integration> Items, string? NextCursor)> ListAsync(
        string? afterCursor,
        int limit,
        CancellationToken cancellationToken)
    {
        DateTimeOffset cursorCreatedAt = default;
        Guid cursorId = default;
        bool hasCursor = afterCursor is not null
            && PageCursor.TryDecode(afterCursor, out cursorCreatedAt, out cursorId);

        IQueryable<Integration> query = context.Integrations.AsNoTracking();
        if (hasCursor)
        {
            query = query.Where(integration =>
                integration.CreatedAt > cursorCreatedAt
                || (integration.CreatedAt == cursorCreatedAt && integration.Id.CompareTo(cursorId) > 0));
        }

        List<Integration> items = await query
            .OrderBy(integration => integration.CreatedAt)
            .ThenBy(integration => integration.Id)
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
}
