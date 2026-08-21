using Integrios.Application.Events;
using MediatR;

namespace Integrios.Application.Recovery;

public sealed record GetEventDeliveryRecoveryQuery(Guid TenantId, Guid EventId)
    : IRequest<EventDto?>;

internal sealed class GetEventDeliveryRecoveryQueryHandler(ITenantEventLookup eventLookup)
    : IRequestHandler<GetEventDeliveryRecoveryQuery, EventDto?>
{
    public Task<EventDto?> Handle(
        GetEventDeliveryRecoveryQuery query,
        CancellationToken cancellationToken) =>
        eventLookup.GetByIdAsync(query.TenantId, query.EventId, cancellationToken);
}
