using Integrios.Application.Abstractions;
using MediatR;

namespace Integrios.Application.Events;

public sealed record ReplayEventCommand(Guid TenantId, Guid EventId)
    : IRequest<bool>;

internal sealed class ReplayEventCommandHandler(IEventRepository eventRepository)
    : IRequestHandler<ReplayEventCommand, bool>
{
    public Task<bool> Handle(ReplayEventCommand command, CancellationToken cancellationToken) =>
        eventRepository.ReplayEventAsync(command.TenantId, command.EventId, cancellationToken);
}
