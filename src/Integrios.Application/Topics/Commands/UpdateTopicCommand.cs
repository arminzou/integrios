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
        var existing = await topicRepository.GetByIdAsync(command.TenantId, command.Id, cancellationToken);
        if (existing is null)
            return null;

        if (string.IsNullOrWhiteSpace(command.Name))
            throw new TopicRequestValidationException("Topic name is required for update.");

        if (!string.Equals(existing.Name, command.Name, StringComparison.Ordinal))
            throw new TopicRequestValidationException(
                "Topic names are immutable; create a new topic to change the stream identifier.");

        var topic = await topicRepository.UpdateAsync(
            command.TenantId,
            command.Id,
            command.Description,
            command.SourceConnectionIds,
            cancellationToken);

        return topic is null ? null : TopicResponse.From(topic);
    }
}
