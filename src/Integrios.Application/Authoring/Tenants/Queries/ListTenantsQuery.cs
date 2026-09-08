using MediatR;
using Integrios.Domain.Enums;

namespace Integrios.Application.Authoring.Tenants;

public sealed record ListTenantsQuery(OperationalStatus? Status, string? Environment, string? Name, string? AfterCursor, int Limit) : IRequest<TenantListDto>;

internal sealed class ListTenantsQueryHandler(ITenantRepository repository)
    : IRequestHandler<ListTenantsQuery, TenantListDto>
{
    public async Task<TenantListDto> Handle(ListTenantsQuery query, CancellationToken cancellationToken)
    {
        var (items, nextCursor) = await repository.ListAsync(query.Status, query.Environment, query.Name, query.AfterCursor, query.Limit, cancellationToken);
        return new TenantListDto
        {
            Items = items.Select(TenantDto.From).ToList(),
            NextCursor = nextCursor,
        };
    }
}
