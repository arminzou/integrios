using Integrios.Application.Authoring.Topics;
using Integrios.Domain.Entities;
using MediatR;

namespace Integrios.Application.Authoring.Sources;

public sealed record DeleteSourceCommand(Guid TenantId, Guid Id) : IRequest<bool>;

internal sealed class DeleteSourceCommandHandler(
    ISourceRepository sourceRepository,
    ITopicRepository topicRepository,
    IAuthoringLock authoringLock) : IRequestHandler<DeleteSourceCommand, bool>
{
    public async Task<bool> Handle(DeleteSourceCommand command, CancellationToken cancellationToken)
    {
        Source? source = await sourceRepository.GetByIdAsync(command.TenantId, command.Id, cancellationToken);
        if (source is null)
            return false;
        await using IAsyncDisposable lease = await authoringLock.AcquireAsync(
            AuthoringResource.Topic,
            [source.TopicId],
            cancellationToken);
        source = await sourceRepository.GetByIdAsync(command.TenantId, command.Id, cancellationToken);
        if (source is null)
            return false;

        IReadOnlyList<SourceDeclaration> remainingSources = (await topicRepository.ListSourceDeclarationsAsync(
                command.TenantId, [source.TopicId], cancellationToken))
            .Where(declaration => declaration.SourceId != source.Id)
            .ToList();
        TopicEventTypes.EnsureNoSelectionLosesItsDeclaration(
            source.EventTypes,
            remainingSources,
            await topicRepository.ListSubscriptionSelectionsAsync(
                command.TenantId, source.TopicId, cancellationToken));

        return await sourceRepository.DeleteAsync(command.TenantId, command.Id, cancellationToken);
    }
}
