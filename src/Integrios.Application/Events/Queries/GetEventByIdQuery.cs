using MediatR;

namespace Integrios.Application.Events;

public sealed record GetEventByIdQuery(Guid TenantId, Guid EventId)
    : IRequest<GetEventResponse?>;

internal sealed class GetEventByIdQueryHandler(ITenantEventLookup eventLookup)
    : IRequestHandler<GetEventByIdQuery, GetEventResponse?>
{
    public Task<GetEventResponse?> Handle(GetEventByIdQuery query, CancellationToken cancellationToken) =>
        eventLookup.GetByIdAsync(query.TenantId, query.EventId, cancellationToken);
}
