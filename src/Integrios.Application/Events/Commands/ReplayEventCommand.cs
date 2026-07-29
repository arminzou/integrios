using Integrios.Application.Delivery;
using MediatR;

namespace Integrios.Application.Events;

public sealed record ReplayEventCommand(Guid TenantId, Guid EventId)
    : IRequest<bool>;

internal sealed class ReplayEventCommandHandler(IDeadLetterReplay deadLetterReplay)
    : IRequestHandler<ReplayEventCommand, bool>
{
    public Task<bool> Handle(ReplayEventCommand command, CancellationToken cancellationToken) =>
        deadLetterReplay.ReplayDeadLetteredAsync(command.TenantId, command.EventId, cancellationToken);
}
