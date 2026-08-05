using MediatR;

namespace Integrios.Application.Events;

public sealed record GetEventByIdQuery(Guid TenantId, Guid EventId)
    : IRequest<EventDto?>;

internal sealed class GetEventByIdQueryHandler(ITenantEventLookup eventLookup)
    : IRequestHandler<GetEventByIdQuery, EventDto?>
{
    public Task<EventDto?> Handle(GetEventByIdQuery query, CancellationToken cancellationToken) =>
        eventLookup.GetByIdAsync(query.TenantId, query.EventId, cancellationToken);
}
