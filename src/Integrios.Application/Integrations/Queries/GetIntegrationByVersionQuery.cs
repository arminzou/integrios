using Integrios.Domain.Integrations;
using MediatR;

namespace Integrios.Application.Integrations;

public sealed record GetIntegrationByVersionQuery(string Key, int ContractVersion) : IRequest<IntegrationResponse?>;

internal sealed class GetIntegrationByVersionQueryHandler(IIntegrationManifestStore store)
    : IRequestHandler<GetIntegrationByVersionQuery, IntegrationResponse?>
{
    public async Task<IntegrationResponse?> Handle(GetIntegrationByVersionQuery query, CancellationToken cancellationToken)
    {
        Integration? integration = await store.GetByVersionAsync(query.Key, query.ContractVersion, cancellationToken);
        return integration is null ? null : IntegrationResponse.From(integration);
    }
}
