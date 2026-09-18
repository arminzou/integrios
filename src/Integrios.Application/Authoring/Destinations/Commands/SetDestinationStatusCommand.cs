using Integrios.Domain.Entities;
using Integrios.Domain.Enums;
using MediatR;

namespace Integrios.Application.Authoring.Destinations;

// A Disabled Destination takes no new delivery snapshots. Deliveries already created keep the request
// they were snapshotted with, so disabling never reaches work already routed.
public sealed record SetDestinationStatusCommand(Guid TenantId, Guid Id, EnablementStatus Status)
    : IRequest<DestinationDto?>;

internal sealed class SetDestinationStatusCommandHandler(
    IDestinationRepository repository,
    IDestinationAuthoringLock authoringLock)
    : IRequestHandler<SetDestinationStatusCommand, DestinationDto?>
{
    public async Task<DestinationDto?> Handle(SetDestinationStatusCommand command, CancellationToken cancellationToken)
    {
        await using IAsyncDisposable lease = await authoringLock.AcquireAsync([command.Id], cancellationToken);
        if (command.Status == EnablementStatus.Disabled
            && await repository.HasActiveSubscriptionsAsync(command.TenantId, command.Id, cancellationToken))
        {
            throw new DestinationValidationException(
                "The Destination cannot be disabled while Enabled Subscriptions reference it.");
        }

        if (!await repository.SetStatusAsync(command.TenantId, command.Id, command.Status, cancellationToken))
            return null;
        Destination? destination = await repository.GetByIdAsync(command.TenantId, command.Id, cancellationToken);
        return destination is null ? null : DestinationDto.From(destination);
    }
}
