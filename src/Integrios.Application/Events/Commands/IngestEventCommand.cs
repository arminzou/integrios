using Integrios.Application.Abstractions;
using MediatR;

namespace Integrios.Application.Events;

public sealed record IngestEventCommand(Guid TenantId, IngestEventRequest Request)
    : IRequest<IngestEventResponse>;

internal sealed class IngestEventCommandHandler(IEventRepository eventRepository)
    : IRequestHandler<IngestEventCommand, IngestEventResponse>
{
    public Task<IngestEventResponse> Handle(IngestEventCommand command, CancellationToken cancellationToken) =>
        eventRepository.IngestAsync(command.TenantId, command.Request, cancellationToken);
}
