using Integrios.Application.Common.Pagination;
using Integrios.Application.Connectors;
using Integrios.Domain.Entities;
using Integrios.Domain.Enums;
using Integrios.Domain.ValueObjects;
using Integrios.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Integrios.Infrastructure.Connectors;

internal sealed class ConnectorCatalog(IntegriosDbContext context) : IConnectorCatalog
{
    public Task<Connector?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
        context.Connectors.AsNoTracking().SingleOrDefaultAsync(
            connector => connector.Id == id,
            cancellationToken);

    public Task<Connector?> GetByVersionAsync(
        string key,
        int contractVersion,
        CancellationToken cancellationToken) =>
        context.Connectors.AsNoTracking().SingleOrDefaultAsync(
            connector => connector.Key == key && connector.ContractVersion == contractVersion,
            cancellationToken);

    public async Task<(IReadOnlyList<Connector> Items, string? NextCursor)> ListAsync(
        string? afterCursor,
        int limit,
        CancellationToken cancellationToken)
    {
        DateTimeOffset cursorCreatedAt = default;
        Guid cursorId = default;
        bool hasCursor = afterCursor is not null
            && PageCursor.TryDecode(afterCursor, out cursorCreatedAt, out cursorId);

        IQueryable<Connector> query = context.Connectors.AsNoTracking();
        if (hasCursor)
        {
            query = query.Where(connector =>
                connector.CreatedAt > cursorCreatedAt
                || (connector.CreatedAt == cursorCreatedAt && connector.Id.CompareTo(cursorId) > 0));
        }

        List<Connector> items = await query
            .OrderBy(connector => connector.CreatedAt)
            .ThenBy(connector => connector.Id)
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
