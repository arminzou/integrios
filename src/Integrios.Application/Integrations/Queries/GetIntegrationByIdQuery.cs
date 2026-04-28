using Integrios.Application.Abstractions;
using Integrios.Domain.Integrations;
using MediatR;

namespace Integrios.Application.Integrations.Queries;

public sealed record GetIntegrationByIdQuery(Guid Id) : IRequest<IntegrationResponse?>;

public sealed class GetIntegrationByIdQueryHandler(IIntegrationRepository repository)
    : IRequestHandler<GetIntegrationByIdQuery, IntegrationResponse?>
{
    public async Task<IntegrationResponse?> Handle(GetIntegrationByIdQuery query, CancellationToken cancellationToken)
    {
        Integration? integration = await repository.GetByIdAsync(query.Id, cancellationToken);
        return integration is null ? null : IntegrationResponse.From(integration);
    }
}
