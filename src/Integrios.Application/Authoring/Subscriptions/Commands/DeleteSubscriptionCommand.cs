using MediatR;

namespace Integrios.Application.Authoring.Subscriptions;

public sealed record DeleteSubscriptionCommand(Guid TenantId, Guid TopicId, Guid Id) : IRequest<bool>;

internal sealed class DeleteSubscriptionCommandHandler(ISubscriptionRepository subscriptionRepository)
    : IRequestHandler<DeleteSubscriptionCommand, bool>
{
    public Task<bool> Handle(DeleteSubscriptionCommand command, CancellationToken cancellationToken) =>
        subscriptionRepository.DeleteAsync(
            command.TenantId, command.TopicId, command.Id, cancellationToken);
}
