using Integrios.Application.Abstractions;
using Integrios.Domain.Integrations;
using MediatR;

namespace Integrios.Application.Connections;

public sealed record ListConnectionsByTenantQuery(Guid TenantId, string? AfterCursor, int Limit) : IRequest<ConnectionListResponse>;

public sealed class ListConnectionsByTenantQueryHandler(IConnectionRepository repository)
    : IRequestHandler<ListConnectionsByTenantQuery, ConnectionListResponse>
{
    public async Task<ConnectionListResponse> Handle(ListConnectionsByTenantQuery query, CancellationToken cancellationToken)
    {
        (IReadOnlyList<Connection> items, string? nextCursor) = await repository.ListByTenantAsync(
            query.TenantId, query.AfterCursor, query.Limit, cancellationToken);

        return new ConnectionListResponse
        {
            Items = items.Select(ConnectionResponse.From).ToList(),
            NextCursor = nextCursor,
        };
    }
}
