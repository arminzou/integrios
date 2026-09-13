using Integrios.Domain.ValueObjects;
using MediatR;

namespace Integrios.Application.Authoring.Topics;

public sealed record CreateTopicCommand(
    Guid TenantId,
    string? Key,
    string? Name,
    string? Description)
    : IRequest<TopicDto>;

internal sealed class CreateTopicCommandHandler(ITopicRepository topicRepository)
    : IRequestHandler<CreateTopicCommand, TopicDto>
{
    public async Task<TopicDto> Handle(CreateTopicCommand command, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command.Key))
            throw new TopicValidationException("Key is required.", field: "key");

        string key = command.Key.Trim();
        if (!ResourceKey.IsValid(key))
        {
            throw new TopicValidationException(
                "Key must be a lowercase DNS label of 1 to 63 characters.",
                field: "key");
        }

        // The label is what an Operator reads, and it starts as the key rather than as nothing, so a
        // Topic authored without one still reads sensibly and can be corrected later.
        string name = string.IsNullOrWhiteSpace(command.Name) ? key : command.Name.Trim();

        var topic = await topicRepository.CreateAsync(
            command.TenantId,
            key,
            name,
            command.Description,
            cancellationToken);
        return TopicDto.From(topic, subscriptionCount: 0);
    }
}
