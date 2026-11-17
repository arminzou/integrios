using Integrios.Application.Authoring;
using Integrios.Domain.Entities;
using MediatR;

namespace Integrios.Application.Authoring.SubscriberKeys;

public sealed record DeleteSubscriberKeyCommand(Guid TenantId, Guid DestinationId, Guid Id) : IRequest<bool>;

internal sealed class DeleteSubscriberKeyCommandHandler(ISubscriberKeyRepository repository)
    : IRequestHandler<DeleteSubscriberKeyCommand, bool>
{
    public async Task<bool> Handle(DeleteSubscriberKeyCommand command, CancellationToken cancellationToken)
    {
        SubscriberKey? key = await repository.GetByIdAsync(command.TenantId, command.DestinationId, command.Id, cancellationToken);
        if (key is null)
            return false;
        if (key.RevokedAt is null)
            throw new AuthoringConflictException("The Subscriber key must be revoked before it can be deleted.");

        return await repository.DeleteAsync(command.TenantId, command.DestinationId, command.Id, cancellationToken);
    }
}
