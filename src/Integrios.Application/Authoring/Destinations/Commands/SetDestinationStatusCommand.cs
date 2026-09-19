using Integrios.Application.Authoring;
using Integrios.Domain.Entities;
using Integrios.Domain.Enums;
using MediatR;

namespace Integrios.Application.Authoring.Destinations;

// An Inactive Destination takes no new delivery snapshots. Deliveries already created keep the request
// they were snapshotted with, so deactivating never reaches work already routed.
public sealed record SetDestinationStatusCommand(Guid TenantId, Guid Id, OperationalStatus Status)
    : IRequest<DestinationDto?>;

internal sealed class SetDestinationStatusCommandHandler(
    IDestinationRepository repository,
    IAuthoringLock authoringLock)
    : IRequestHandler<SetDestinationStatusCommand, DestinationDto?>
{
    public async Task<DestinationDto?> Handle(SetDestinationStatusCommand command, CancellationToken cancellationToken)
    {
        await using IAsyncDisposable lease = await authoringLock.AcquireAsync(
            AuthoringResource.Destination, [command.Id], cancellationToken);
        if (command.Status == OperationalStatus.Inactive
            && await repository.HasActiveSubscriptionsAsync(command.TenantId, command.Id, cancellationToken))
        {
            throw new AuthoringConflictException(
                "The Destination cannot be deactivated while Active Subscriptions reference it.");
        }

        if (!await repository.SetStatusAsync(command.TenantId, command.Id, command.Status, cancellationToken))
            return null;
        Destination? destination = await repository.GetByIdAsync(command.TenantId, command.Id, cancellationToken);
        return destination is null ? null : DestinationDto.From(destination);
    }
}
