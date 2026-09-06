using MediatR;

namespace Integrios.Application.Delivery;

public sealed record GetEventDeliveryRecoveryQuery(Guid TenantId, Guid EventId)
    : IRequest<EventDiagnosticsDto?>;

internal sealed class GetEventDeliveryRecoveryQueryHandler(IEventDiagnosticsLookup diagnostics)
    : IRequestHandler<GetEventDeliveryRecoveryQuery, EventDiagnosticsDto?>
{
    public Task<EventDiagnosticsDto?> Handle(
        GetEventDeliveryRecoveryQuery query,
        CancellationToken cancellationToken) =>
        diagnostics.GetAsync(query.TenantId, query.EventId, cancellationToken);
}
