using Integrios.Application.Authoring;
using MediatR;

namespace Integrios.Application.Authoring.Topics;

public sealed record DeleteTopicCommand(Guid TenantId, Guid Id) : IRequest<bool>;

internal sealed class DeleteTopicCommandHandler(
    ITopicRepository topicRepository,
    IAuthoringLock authoringLock)
    : IRequestHandler<DeleteTopicCommand, bool>
{
    public async Task<bool> Handle(DeleteTopicCommand command, CancellationToken cancellationToken)
    {
        await using IAsyncDisposable lease = await authoringLock.AcquireAsync(
            AuthoringResource.Topic,
            [command.Id],
            cancellationToken);
        if (await topicRepository.GetByIdAsync(command.TenantId, command.Id, cancellationToken) is null)
            return false;
        if (await topicRepository.CountSourcesAsync(command.TenantId, command.Id, cancellationToken) > 0
            || await topicRepository.CountSubscriptionsAsync(command.TenantId, command.Id, cancellationToken) > 0)
        {
            throw new AuthoringConflictException(
                "The Topic cannot be deleted while Sources or Subscriptions reference it.");
        }

        return await topicRepository.DeleteAsync(command.TenantId, command.Id, cancellationToken);
    }
}
