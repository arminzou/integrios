using Integrios.Application.Authoring;
using Integrios.Application.Authoring.Destinations;
using Integrios.Domain.Entities;
using Integrios.Domain.Enums;
using MediatR;

namespace Integrios.Application.Authoring.Subscriptions;

// A Disabled Subscription is absent from fanout and accrues no backlog; enabling it affects only
// Events routed afterwards.
public sealed record SetSubscriptionStatusCommand(Guid TenantId, Guid TopicId, Guid Id, EnablementStatus Status)
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
        if (command.Status == EnablementStatus.Enabled)
        {
            Destination? destination = await destinationRepository.GetByIdAsync(
                command.TenantId, existing.DestinationId, cancellationToken);
            if (destination?.Status != EnablementStatus.Enabled)
            {
                throw new AuthoringConflictException(
                    "The Subscription cannot be enabled while its Destination is Disabled.");
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
