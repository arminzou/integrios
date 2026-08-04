using MediatR;

namespace Integrios.Application.Topics;

public sealed record GetTopicByIdQuery(Guid TenantId, Guid Id) : IRequest<TopicDto?>;

internal sealed class GetTopicByIdQueryHandler(ITopicRepository topicRepository)
    : IRequestHandler<GetTopicByIdQuery, TopicDto?>
{
    public async Task<TopicDto?> Handle(GetTopicByIdQuery query, CancellationToken cancellationToken)
    {
        var topic = await topicRepository.GetByIdAsync(query.TenantId, query.Id, cancellationToken);
        return topic is null ? null : TopicDto.From(topic);
    }
}
