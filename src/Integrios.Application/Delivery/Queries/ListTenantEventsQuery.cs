using Integrios.Application.Ingestion;
using MediatR;

namespace Integrios.Application.Delivery;

public sealed record ListTenantEventsQuery(Guid TenantId, TenantEventFilter Filter, string? AfterCursor, int Limit)
    : IRequest<EventListDto>;

public sealed record EventListDto(IReadOnlyList<EventListItemDto> Items, string? NextCursor);

internal sealed class ListTenantEventsQueryHandler(ITenantEventHistory eventHistory)
    : IRequestHandler<ListTenantEventsQuery, EventListDto>
{
    public async Task<EventListDto> Handle(ListTenantEventsQuery query, CancellationToken cancellationToken)
    {
        var (items, nextCursor) = await eventHistory.ListAsync(
            query.TenantId, query.Filter, query.AfterCursor, query.Limit, cancellationToken);
        return new EventListDto(items, nextCursor);
    }
}
