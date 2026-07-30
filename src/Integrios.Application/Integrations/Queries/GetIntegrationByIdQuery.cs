using Integrios.Domain.Integrations;
using MediatR;

namespace Integrios.Application.Integrations;

public sealed record GetIntegrationByIdQuery(Guid Id) : IRequest<IntegrationResponse?>;

internal sealed class GetIntegrationByIdQueryHandler(IIntegrationCatalog integrationCatalog)
    : IRequestHandler<GetIntegrationByIdQuery, IntegrationResponse?>
{
    public async Task<IntegrationResponse?> Handle(GetIntegrationByIdQuery query, CancellationToken cancellationToken)
    {
        Integration? integration = await integrationCatalog.GetByIdAsync(query.Id, cancellationToken);
        return integration is null ? null : IntegrationResponse.From(integration);
    }
}
