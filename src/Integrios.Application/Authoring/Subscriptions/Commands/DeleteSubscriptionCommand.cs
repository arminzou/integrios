using MediatR;

namespace Integrios.Application.Authoring.Subscriptions;

public sealed record DeleteSubscriptionCommand(Guid TenantId, Guid TopicId, Guid Id) : IRequest<bool>;

internal sealed class DeleteSubscriptionCommandHandler(
    ISubscriptionRepository subscriptionRepository,
    IAuthoringLock authoringLock)
    : IRequestHandler<DeleteSubscriptionCommand, bool>
{
    public async Task<bool> Handle(DeleteSubscriptionCommand command, CancellationToken cancellationToken)
    {
        await using IAsyncDisposable lease = await authoringLock.AcquireAsync(
            AuthoringResource.Topic,
            [command.TopicId],
            cancellationToken);
        return await subscriptionRepository.DeleteAsync(
            command.TenantId, command.TopicId, command.Id, cancellationToken);
    }
}
