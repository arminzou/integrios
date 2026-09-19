using Integrios.Application.Ingestion;
using MediatR;

namespace Integrios.Application.Delivery;

public sealed record GetTenantEventBacklogQuery(Guid TenantId) : IRequest<EventBacklogDto>;

internal sealed class GetTenantEventBacklogQueryHandler(ITenantEventMonitoring monitoring)
    : IRequestHandler<GetTenantEventBacklogQuery, EventBacklogDto>
{
    public Task<EventBacklogDto> Handle(GetTenantEventBacklogQuery query, CancellationToken cancellationToken) =>
        monitoring.GetBacklogAsync(query.TenantId, cancellationToken);
}
