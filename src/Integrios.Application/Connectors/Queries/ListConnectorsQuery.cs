using Integrios.Domain.Entities;
using Integrios.Domain.Enums;
using Integrios.Domain.ValueObjects;
using MediatR;

namespace Integrios.Application.Connectors;

public sealed record ListConnectorsQuery(string? AfterCursor, int Limit) : IRequest<ConnectorListDto>;

internal sealed class ListConnectorsQueryHandler(IConnectorCatalog connectorCatalog)
    : IRequestHandler<ListConnectorsQuery, ConnectorListDto>
{
    public async Task<ConnectorListDto> Handle(ListConnectorsQuery query, CancellationToken cancellationToken)
    {
        (IReadOnlyList<Connector> items, string? nextCursor) = await connectorCatalog.ListAsync(
            query.AfterCursor, query.Limit, cancellationToken);

        return new ConnectorListDto
        {
            Items = items.Select(ConnectorDto.From).ToList(),
            NextCursor = nextCursor,
        };
    }
}
