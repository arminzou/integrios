using Integrios.Domain.Integrations;
using MediatR;

namespace Integrios.Application.Integrations;

public sealed record GetIntegrationByIdQuery(Guid Id) : IRequest<IntegrationDto?>;

internal sealed class GetIntegrationByIdQueryHandler(IIntegrationCatalog integrationCatalog)
    : IRequestHandler<GetIntegrationByIdQuery, IntegrationDto?>
{
    public async Task<IntegrationDto?> Handle(GetIntegrationByIdQuery query, CancellationToken cancellationToken)
    {
        Integration? integration = await integrationCatalog.GetByIdAsync(query.Id, cancellationToken);
        return integration is null ? null : IntegrationDto.From(integration);
    }
}
