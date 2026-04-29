using Integrios.Application.Abstractions;
using MediatR;

namespace Integrios.Application.Subscriptions;

public sealed record DeactivateSubscriptionCommand(Guid TenantId, Guid TopicId, Guid Id) : IRequest<bool>;

internal sealed class DeactivateSubscriptionCommandHandler(ISubscriptionRepository subscriptionRepository)
    : IRequestHandler<DeactivateSubscriptionCommand, bool>
{
    public Task<bool> Handle(DeactivateSubscriptionCommand command, CancellationToken cancellationToken) =>
        subscriptionRepository.DeactivateAsync(command.TenantId, command.TopicId, command.Id, cancellationToken);
}
