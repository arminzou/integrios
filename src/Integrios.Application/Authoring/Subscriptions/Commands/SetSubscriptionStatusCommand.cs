using Integrios.Domain.Entities;
using Integrios.Domain.Enums;
using MediatR;

namespace Integrios.Application.Authoring.Subscriptions;

// A Disabled Subscription is absent from fanout and accrues no backlog; enabling it affects only
// Events routed afterwards.
public sealed record SetSubscriptionStatusCommand(Guid TenantId, Guid TopicId, Guid Id, EnablementStatus Status)
    : IRequest<SubscriptionDto?>;

internal sealed class SetSubscriptionStatusCommandHandler(ISubscriptionRepository subscriptionRepository)
    : IRequestHandler<SetSubscriptionStatusCommand, SubscriptionDto?>
{
    public async Task<SubscriptionDto?> Handle(SetSubscriptionStatusCommand command, CancellationToken cancellationToken)
    {
        if (!await subscriptionRepository.SetStatusAsync(
                command.TenantId, command.TopicId, command.Id, command.Status, cancellationToken))
            return null;
        Subscription? subscription = await subscriptionRepository.GetByIdAsync(
            command.TenantId, command.TopicId, command.Id, cancellationToken);
        return subscription is null ? null : SubscriptionDto.From(subscription);
    }
}
