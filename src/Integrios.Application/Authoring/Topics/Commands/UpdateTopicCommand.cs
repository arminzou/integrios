using MediatR;

namespace Integrios.Application.Authoring.Topics;

public sealed record UpdateTopicCommand(
    Guid TenantId,
    Guid Id,
    string? Name,
    string? Description)
    : IRequest<TopicDto?>;

internal sealed class UpdateTopicCommandHandler(ITopicRepository topicRepository)
    : IRequestHandler<UpdateTopicCommand, TopicDto?>
{
    public async Task<TopicDto?> Handle(UpdateTopicCommand command, CancellationToken cancellationToken)
    {
        var existing = await topicRepository.GetByIdAsync(command.TenantId, command.Id, cancellationToken);
        if (existing is null)
            return null;

        var topic = await topicRepository.UpdateAsync(
            command.TenantId,
            command.Id,
            command.Name,
            command.Description,
            cancellationToken);

        return topic is null ? null : TopicDto.From(topic);
    }
}
