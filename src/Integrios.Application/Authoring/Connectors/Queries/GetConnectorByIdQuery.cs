using Integrios.Domain.Entities;
using Integrios.Domain.Enums;
using Integrios.Domain.ValueObjects;
using MediatR;

namespace Integrios.Application.Authoring.Connectors;

public sealed record GetConnectorByIdQuery(Guid Id) : IRequest<ConnectorDto?>;

internal sealed class GetConnectorByIdQueryHandler(IConnectorReader connectorReader)
    : IRequestHandler<GetConnectorByIdQuery, ConnectorDto?>
{
    public async Task<ConnectorDto?> Handle(GetConnectorByIdQuery query, CancellationToken cancellationToken)
    {
        Connector? connector = await connectorReader.GetByIdAsync(query.Id, cancellationToken);
        return connector is null ? null : ConnectorDto.From(connector);
    }
}
