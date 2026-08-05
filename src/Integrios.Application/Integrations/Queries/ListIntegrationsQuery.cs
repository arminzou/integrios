using Integrios.Domain.Integrations;
using MediatR;

namespace Integrios.Application.Integrations;

public sealed record ListIntegrationsQuery(string? AfterCursor, int Limit) : IRequest<IntegrationListDto>;

internal sealed class ListIntegrationsQueryHandler(IIntegrationCatalog integrationCatalog)
    : IRequestHandler<ListIntegrationsQuery, IntegrationListDto>
{
    public async Task<IntegrationListDto> Handle(ListIntegrationsQuery query, CancellationToken cancellationToken)
    {
        (IReadOnlyList<Integration> items, string? nextCursor) = await integrationCatalog.ListAsync(
            query.AfterCursor, query.Limit, cancellationToken);

        return new IntegrationListDto
        {
            Items = items.Select(IntegrationDto.From).ToList(),
            NextCursor = nextCursor,
        };
    }
}
