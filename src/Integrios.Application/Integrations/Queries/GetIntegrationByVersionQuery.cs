using Integrios.Domain.Integrations;
using MediatR;

namespace Integrios.Application.Integrations;

public sealed record GetIntegrationByVersionQuery(string Key, int ContractVersion) : IRequest<IntegrationDto?>;

internal sealed class GetIntegrationByVersionQueryHandler(IIntegrationManifestStore store)
    : IRequestHandler<GetIntegrationByVersionQuery, IntegrationDto?>
{
    public async Task<IntegrationDto?> Handle(GetIntegrationByVersionQuery query, CancellationToken cancellationToken)
    {
        Integration? integration = await store.GetByVersionAsync(query.Key, query.ContractVersion, cancellationToken);
        return integration is null ? null : IntegrationDto.From(integration);
    }
}
