using Integrios.Domain.ValueObjects;
using MediatR;

namespace Integrios.Application.Authoring.Connections;

public sealed record ListConnectionsByTenantQuery(Guid TenantId, ConnectionListFilter Filter, string? AfterCursor, int Limit) : IRequest<ConnectionListDto>;

internal sealed class ListConnectionsByTenantQueryHandler(IConnectionRepository repository)
    : IRequestHandler<ListConnectionsByTenantQuery, ConnectionListDto>
{
    public async Task<ConnectionListDto> Handle(ListConnectionsByTenantQuery query, CancellationToken cancellationToken)
    {
        (IReadOnlyList<ConnectionListRow> items, string? nextCursor) = await repository.ListByTenantAsync(
            query.TenantId, query.Filter, query.AfterCursor, query.Limit, cancellationToken);

        return new ConnectionListDto
        {
            Items = items.Select(row => ConnectionListItemDto.From(row.Connection, row.ConnectorKey)).ToList(),
            NextCursor = nextCursor,
        };
    }
}
