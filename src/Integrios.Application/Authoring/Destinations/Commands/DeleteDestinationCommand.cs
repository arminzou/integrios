using Integrios.Application.Authoring;
using MediatR;

namespace Integrios.Application.Authoring.Destinations;

public sealed record DeleteDestinationCommand(Guid TenantId, Guid Id) : IRequest<bool>;

internal sealed class DeleteDestinationCommandHandler(
    IDestinationRepository repository,
    IAuthoringLock authoringLock) : IRequestHandler<DeleteDestinationCommand, bool>
{
    public async Task<bool> Handle(DeleteDestinationCommand command, CancellationToken cancellationToken)
    {
        await using IAsyncDisposable lease = await authoringLock.AcquireAsync(
            AuthoringResource.Destination, [command.Id], cancellationToken);
        if (await repository.GetByIdAsync(command.TenantId, command.Id, cancellationToken) is null)
            return false;
        if (await repository.HasSubscriptionsAsync(command.TenantId, command.Id, cancellationToken))
            throw new AuthoringConflictException(
                "The Destination cannot be deleted while Subscriptions reference it.");

        return await repository.DeleteAsync(command.TenantId, command.Id, cancellationToken);
    }
}
