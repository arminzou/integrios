using Integrios.Application.Abstractions;
using Integrios.Domain.Contracts;
using MediatR;

namespace Integrios.Application.Events;

public sealed record GetEventByIdQuery(Guid TenantId, Guid EventId)
    : IRequest<GetEventResponse?>;

internal sealed class GetEventByIdQueryHandler(IEventRepository eventRepository)
    : IRequestHandler<GetEventByIdQuery, GetEventResponse?>
{
    public Task<GetEventResponse?> Handle(GetEventByIdQuery query, CancellationToken cancellationToken) =>
        eventRepository.GetEventByIdAsync(query.TenantId, query.EventId, cancellationToken);
}
