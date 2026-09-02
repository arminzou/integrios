using Integrios.Domain.Entities;
using Integrios.Domain.ValueObjects;
using Integrios.Domain.Enums;
using MediatR;

namespace Integrios.Application.Authoring.Connections;

public sealed record ListConnectionsByTenantQuery(Guid TenantId, OperationalStatus? Status, string? AfterCursor, int Limit) : IRequest<ConnectionListDto>;

internal sealed class ListConnectionsByTenantQueryHandler(IConnectionRepository repository)
    : IRequestHandler<ListConnectionsByTenantQuery, ConnectionListDto>
{
    public async Task<ConnectionListDto> Handle(ListConnectionsByTenantQuery query, CancellationToken cancellationToken)
    {
        (IReadOnlyList<Connection> items, string? nextCursor) = await repository.ListByTenantAsync(
            query.TenantId, query.Status, query.AfterCursor, query.Limit, cancellationToken);

        return new ConnectionListDto
        {
            Items = items.Select(ConnectionListItemDto.From).ToList(),
            NextCursor = nextCursor,
        };
    }
}
