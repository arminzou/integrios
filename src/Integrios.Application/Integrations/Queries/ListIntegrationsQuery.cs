using Integrios.Domain.Integrations;
using MediatR;

namespace Integrios.Application.Integrations;

public sealed record ListIntegrationsQuery(string? AfterCursor, int Limit) : IRequest<IntegrationListResponse>;

internal sealed class ListIntegrationsQueryHandler(IIntegrationCatalog integrationCatalog)
    : IRequestHandler<ListIntegrationsQuery, IntegrationListResponse>
{
    public async Task<IntegrationListResponse> Handle(ListIntegrationsQuery query, CancellationToken cancellationToken)
    {
        (IReadOnlyList<Integration> items, string? nextCursor) = await integrationCatalog.ListAsync(
            query.AfterCursor, query.Limit, cancellationToken);

        return new IntegrationListResponse
        {
            Items = items.Select(IntegrationResponse.From).ToList(),
            NextCursor = nextCursor,
        };
    }
}
