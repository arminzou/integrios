using Integrios.Domain.Connections;
using MediatR;

namespace Integrios.Application.Connections;

public sealed record ListConnectionsByTenantQuery(Guid TenantId, string? AfterCursor, int Limit) : IRequest<ConnectionListDto>;

internal sealed class ListConnectionsByTenantQueryHandler(IConnectionRepository repository)
    : IRequestHandler<ListConnectionsByTenantQuery, ConnectionListDto>
{
    public async Task<ConnectionListDto> Handle(ListConnectionsByTenantQuery query, CancellationToken cancellationToken)
    {
        (IReadOnlyList<Connection> items, string? nextCursor) = await repository.ListByTenantAsync(
            query.TenantId, query.AfterCursor, query.Limit, cancellationToken);

        return new ConnectionListDto
        {
            Items = items.Select(ConnectionDto.From).ToList(),
            NextCursor = nextCursor,
        };
    }
}
