using MediatR;

namespace Integrios.Application.Authoring.Tenants;

public sealed record ListTenantsQuery(string? AfterCursor, int Limit) : IRequest<TenantListDto>;

internal sealed class ListTenantsQueryHandler(ITenantRepository repository)
    : IRequestHandler<ListTenantsQuery, TenantListDto>
{
    public async Task<TenantListDto> Handle(ListTenantsQuery query, CancellationToken cancellationToken)
    {
        var (items, nextCursor) = await repository.ListAsync(query.AfterCursor, query.Limit, cancellationToken);
        return new TenantListDto
        {
            Items = items.Select(TenantDto.From).ToList(),
            NextCursor = nextCursor,
        };
    }
}
