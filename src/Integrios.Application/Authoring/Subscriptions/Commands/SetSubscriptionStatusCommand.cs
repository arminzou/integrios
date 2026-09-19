using Integrios.Application.Authoring;
using Integrios.Application.Authoring.Destinations;
using Integrios.Domain.Entities;
using Integrios.Domain.Enums;
using MediatR;

namespace Integrios.Application.Authoring.Subscriptions;

// An Inactive Subscription is absent from fanout and accrues no backlog; activating it affects only
// Events routed afterwards.
public sealed record SetSubscriptionStatusCommand(Guid TenantId, Guid TopicId, Guid Id, OperationalStatus Status)
    : IRequest<SubscriptionDto?>;

internal sealed class SetSubscriptionStatusCommandHandler(
    ISubscriptionRepository subscriptionRepository,
    IDestinationRepository destinationRepository,
    IAuthoringLock authoringLock)
    : IRequestHandler<SetSubscriptionStatusCommand, SubscriptionDto?>
{
    public async Task<SubscriptionDto?> Handle(SetSubscriptionStatusCommand command, CancellationToken cancellationToken)
    {
        Subscription? existing = await subscriptionRepository.GetByIdAsync(
            command.TenantId, command.TopicId, command.Id, cancellationToken);
        if (existing is null)
            return null;

        await using IAsyncDisposable lease = await authoringLock.AcquireAsync(
            AuthoringResource.Destination,
            [existing.DestinationId], cancellationToken);
        if (command.Status == OperationalStatus.Active)
        {
            Destination? destination = await destinationRepository.GetByIdAsync(
                command.TenantId, existing.DestinationId, cancellationToken);
            if (destination?.Status != OperationalStatus.Active)
            {
                throw new AuthoringConflictException(
                    "The Subscription cannot be activated while its Destination is Inactive.");
            }
        }

        if (!await subscriptionRepository.SetStatusAsync(
                command.TenantId, command.TopicId, command.Id, command.Status, cancellationToken))
            return null;
        Subscription? subscription = await subscriptionRepository.GetByIdAsync(
            command.TenantId, command.TopicId, command.Id, cancellationToken);
        return subscription is null ? null : SubscriptionDto.From(subscription);
    }
}
