using Integrios.Application.Abstractions;
using MediatR;

namespace Integrios.Application.Topics;

public sealed record CreateTopicCommand(
    Guid TenantId,
    string Name,
    string? Description,
    IReadOnlyList<Guid> SourceConnectionIds)
    : IRequest<TopicResponse>;

internal sealed class CreateTopicCommandHandler(ITopicRepository topicRepository)
    : IRequestHandler<CreateTopicCommand, TopicResponse>
{
    public async Task<TopicResponse> Handle(CreateTopicCommand command, CancellationToken cancellationToken)
    {
        var topic = await topicRepository.CreateAsync(
            command.TenantId,
            command.Name,
            command.Description,
            command.SourceConnectionIds,
            cancellationToken);
        return TopicResponse.From(topic);
    }
}
