using Integrios.Domain.Entities;
using Integrios.Domain.Enums;
using Integrios.Domain.ValueObjects;
using MediatR;

namespace Integrios.Application.Authoring.Connectors;

public sealed record ListConnectorsQuery(string? AfterCursor, int Limit) : IRequest<ConnectorListDto>;

internal sealed class ListConnectorsQueryHandler(IConnectorReader connectorReader)
    : IRequestHandler<ListConnectorsQuery, ConnectorListDto>
{
    public async Task<ConnectorListDto> Handle(ListConnectorsQuery query, CancellationToken cancellationToken)
    {
        (IReadOnlyList<Connector> items, string? nextCursor) = await connectorReader.ListAsync(
            query.AfterCursor, query.Limit, cancellationToken);

        return new ConnectorListDto
        {
            Items = items.Select(ConnectorDto.From).ToList(),
            NextCursor = nextCursor,
        };
    }
}
