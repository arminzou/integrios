using MediatR;

namespace Integrios.Application.EventMonitoring;

public sealed record ListTenantEventsQuery(Guid TenantId, TenantEventFilter Filter, string? AfterCursor, int Limit)
    : IRequest<EventListDto>;

/// Watermark is present on a first page only; the freshness read counts what arrived after it.
public sealed record EventListDto(IReadOnlyList<EventListItemDto> Items, string? NextCursor, string? Watermark);

internal sealed class ListTenantEventsQueryHandler(ITenantEventHistory eventHistory)
    : IRequestHandler<ListTenantEventsQuery, EventListDto>
{
    public async Task<EventListDto> Handle(ListTenantEventsQuery query, CancellationToken cancellationToken)
    {
        var (items, nextCursor, watermark) = await eventHistory.ListAsync(
            query.TenantId, query.Filter, query.AfterCursor, query.Limit, cancellationToken);
        return new EventListDto(items, nextCursor, watermark);
    }
}
