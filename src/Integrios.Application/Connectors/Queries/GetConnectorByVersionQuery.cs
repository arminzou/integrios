using Integrios.Domain.Entities;
using Integrios.Domain.Enums;
using Integrios.Domain.ValueObjects;
using MediatR;

namespace Integrios.Application.Connectors;

public sealed record GetConnectorByVersionQuery(string Key, int ContractVersion) : IRequest<ConnectorDto?>;

internal sealed class GetConnectorByVersionQueryHandler(IConnectorManifestStore store)
    : IRequestHandler<GetConnectorByVersionQuery, ConnectorDto?>
{
    public async Task<ConnectorDto?> Handle(GetConnectorByVersionQuery query, CancellationToken cancellationToken)
    {
        Connector? connector = await store.GetByVersionAsync(query.Key, query.ContractVersion, cancellationToken);
        return connector is null ? null : ConnectorDto.From(connector);
    }
}
