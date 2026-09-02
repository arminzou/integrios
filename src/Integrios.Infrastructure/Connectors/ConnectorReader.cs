using Integrios.Application.Common.Exceptions;
using Integrios.Application.Authoring.Connectors;
using Integrios.Infrastructure.Common.Pagination;
using Integrios.Domain.Entities;
using Integrios.Domain.Enums;
using Integrios.Domain.ValueObjects;
using Integrios.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.DataProtection;

namespace Integrios.Infrastructure.Connectors;

internal sealed class ConnectorReader(IntegriosDbContext context, IDataProtectionProvider dataProtectionProvider) : IConnectorReader
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
        ConnectorDirection? direction,
        string? afterCursor,
        int limit,
        CancellationToken cancellationToken)
    {
        DateTimeOffset cursorCreatedAt = default;
        Guid cursorId = default;
        string cursorScope = $"connectors:{direction?.ToString() ?? "all"}";
        bool hasCursor = afterCursor is not null;
        if (hasCursor && !PageCursor.TryDecode(dataProtectionProvider, afterCursor!, cursorScope, out cursorCreatedAt, out cursorId))
            throw new InvalidCursorException();

        IQueryable<Connector> query = context.Connectors.AsNoTracking();
        if (direction is not null)
            query = query.Where(connector => connector.Direction == direction);
        if (hasCursor)
        {
            query = query.Where(connector =>
                connector.CreatedAt < cursorCreatedAt
                || (connector.CreatedAt == cursorCreatedAt && connector.Id.CompareTo(cursorId) < 0));
        }

        List<Connector> items = await query
            .OrderByDescending(connector => connector.CreatedAt)
            .ThenByDescending(connector => connector.Id)
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
}
