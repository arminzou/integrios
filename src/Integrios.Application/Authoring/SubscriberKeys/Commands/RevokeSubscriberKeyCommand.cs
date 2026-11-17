using MediatR;

namespace Integrios.Application.Authoring.SubscriberKeys;

public sealed record RevokeSubscriberKeyCommand(Guid TenantId, Guid DestinationId, Guid Id) : IRequest<bool>;

internal sealed class RevokeSubscriberKeyCommandHandler(ISubscriberKeyRepository repository)
    : IRequestHandler<RevokeSubscriberKeyCommand, bool>
{
    public Task<bool> Handle(RevokeSubscriberKeyCommand command, CancellationToken cancellationToken)
        => repository.RevokeAsync(command.TenantId, command.DestinationId, command.Id, cancellationToken);
}
