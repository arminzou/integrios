using MediatR;

namespace Integrios.Application.Authoring.Topics;

public sealed record CreateTopicCommand(
    Guid TenantId,
    string? Name,
    string? Description)
    : IRequest<TopicDto>;

internal sealed class CreateTopicCommandHandler(ITopicRepository topicRepository)
    : IRequestHandler<CreateTopicCommand, TopicDto>
{
    public async Task<TopicDto> Handle(CreateTopicCommand command, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command.Name))
            throw new TopicValidationException("Name is required.", field: "name");

        var topic = await topicRepository.CreateAsync(
            command.TenantId,
            command.Name,
            command.Description,
            cancellationToken);
        return TopicDto.From(topic);
    }
}
