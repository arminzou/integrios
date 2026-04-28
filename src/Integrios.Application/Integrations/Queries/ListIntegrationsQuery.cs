using Integrios.Application.Abstractions;
using Integrios.Domain.Integrations;
using MediatR;

namespace Integrios.Application.Integrations;

public sealed record ListIntegrationsQuery(string? AfterCursor, int Limit) : IRequest<IntegrationListResponse>;

public sealed class ListIntegrationsQueryHandler(IIntegrationRepository repository)
    : IRequestHandler<ListIntegrationsQuery, IntegrationListResponse>
{
    public async Task<IntegrationListResponse> Handle(ListIntegrationsQuery query, CancellationToken cancellationToken)
    {
        (IReadOnlyList<Integration> items, string? nextCursor) = await repository.ListAsync(
            query.AfterCursor, query.Limit, cancellationToken);

        return new IntegrationListResponse
        {
            Items = items.Select(IntegrationResponse.From).ToList(),
            NextCursor = nextCursor,
        };
    }
}
