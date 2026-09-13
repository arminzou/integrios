using MediatR;

namespace Integrios.Application.Authoring.Destinations;

public sealed record DeactivateDestinationCommand(Guid TenantId, Guid Id) : IRequest<bool>;

internal sealed class DeactivateDestinationCommandHandler(
    IDestinationRepository repository,
    IDestinationAuthoringLock authoringLock)
    : IRequestHandler<DeactivateDestinationCommand, bool>
{
    public async Task<bool> Handle(DeactivateDestinationCommand command, CancellationToken cancellationToken)
    {
        await using IAsyncDisposable lease = await authoringLock.AcquireAsync([command.Id], cancellationToken);
        if (await repository.HasActiveSubscriptionsAsync(command.TenantId, command.Id, cancellationToken))
        {
            throw new DestinationValidationException(
                "The Destination cannot be deactivated while active Subscriptions reference it.");
        }

        return await repository.DeactivateAsync(command.TenantId, command.Id, cancellationToken);
    }
}
