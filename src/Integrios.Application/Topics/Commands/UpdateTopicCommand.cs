using Integrios.Application.Abstractions;
using MediatR;

namespace Integrios.Application.Topics;

public sealed record UpdateTopicCommand(
    Guid TenantId,
    Guid Id,
    string? Name,
    string? Description,
    IReadOnlyList<Guid>? SourceConnectionIds)
    : IRequest<TopicResponse?>;

internal sealed class UpdateTopicCommandHandler(ITopicRepository topicRepository)
    : IRequestHandler<UpdateTopicCommand, TopicResponse?>
{
    public async Task<TopicResponse?> Handle(UpdateTopicCommand command, CancellationToken cancellationToken)
    {
        var topic = await topicRepository.UpdateAsync(
            command.TenantId,
            command.Id,
            command.Name,
            command.Description,
            command.SourceConnectionIds,
            cancellationToken);

        return topic is null ? null : TopicResponse.From(topic);
    }
}
