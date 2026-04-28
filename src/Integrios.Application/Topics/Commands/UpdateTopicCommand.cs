using Integrios.Application.Abstractions;
using MediatR;

namespace Integrios.Application.Topics;

public sealed record UpdateTopicCommand(
    Guid TenantId,
    Guid Id,
    string Name,
    string? Description,
    IReadOnlyList<Guid>? SourceConnectionIds)
    : IRequest<TopicResponse?>;

internal sealed class UpdateTopicCommandHandler(ITopicRepository topicRepository)
    : IRequestHandler<UpdateTopicCommand, TopicResponse?>
{
    public async Task<TopicResponse?> Handle(UpdateTopicCommand command, CancellationToken cancellationToken)
    {
        var topic = await topicRepository.UpdateAsync(
            command.TenantId, command.Id, command.Name, command.Description, cancellationToken);

        if (topic is null)
            return null;

        if (command.SourceConnectionIds is not null)
            await topicRepository.SetSourceConnectionsAsync(
                command.TenantId, command.Id, command.SourceConnectionIds, cancellationToken);

        return TopicResponse.From(topic with
        {
            SourceConnectionIds = command.SourceConnectionIds ?? topic.SourceConnectionIds
        });
    }
}
