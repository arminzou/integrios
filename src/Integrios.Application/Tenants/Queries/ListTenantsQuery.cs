using Integrios.Application.Abstractions;
using MediatR;

namespace Integrios.Application.Tenants.Queries;

public sealed record ListTenantsQuery(string? AfterCursor, int Limit) : IRequest<TenantListResponse>;

public sealed class ListTenantsQueryHandler(ITenantRepository repository)
    : IRequestHandler<ListTenantsQuery, TenantListResponse>
{
    public async Task<TenantListResponse> Handle(ListTenantsQuery query, CancellationToken cancellationToken)
    {
        var (items, nextCursor) = await repository.ListAsync(query.AfterCursor, query.Limit, cancellationToken);
        return new TenantListResponse
        {
            Items = items.Select(TenantResponse.From).ToList(),
            NextCursor = nextCursor,
        };
    }
}
