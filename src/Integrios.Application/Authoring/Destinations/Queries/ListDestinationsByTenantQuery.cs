using MediatR;

namespace Integrios.Application.Authoring.Destinations;

public sealed record ListDestinationsByTenantQuery(
    Guid TenantId,
    DestinationListFilter Filter,
    string? AfterCursor,
    int Limit) : IRequest<DestinationListDto>;

internal sealed class ListDestinationsByTenantQueryHandler(IDestinationRepository repository)
    : IRequestHandler<ListDestinationsByTenantQuery, DestinationListDto>
{
    public async Task<DestinationListDto> Handle(
        ListDestinationsByTenantQuery query,
        CancellationToken cancellationToken)
    {
        (IReadOnlyList<DestinationListRow> items, string? nextCursor) = await repository.ListByTenantAsync(
            query.TenantId,
            query.Filter,
            query.AfterCursor,
            query.Limit,
            cancellationToken);
        return new DestinationListDto
        {
            Items = items.Select(row => DestinationListItemDto.From(row.Destination, row.ConnectorKey)).ToList(),
            NextCursor = nextCursor,
        };
    }
}
