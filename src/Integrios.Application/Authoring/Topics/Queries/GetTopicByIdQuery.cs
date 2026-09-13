using MediatR;

namespace Integrios.Application.Authoring.Topics;

public sealed record GetTopicByIdQuery(Guid TenantId, Guid Id) : IRequest<TopicDto?>;

internal sealed class GetTopicByIdQueryHandler(ITopicRepository topicRepository)
    : IRequestHandler<GetTopicByIdQuery, TopicDto?>
{
    public async Task<TopicDto?> Handle(GetTopicByIdQuery query, CancellationToken cancellationToken)
    {
        var topic = await topicRepository.GetByIdAsync(query.TenantId, query.Id, cancellationToken);
        if (topic is null)
            return null;
        int subscriptions = await topicRepository.CountSubscriptionsAsync(
            query.TenantId, topic.Id, cancellationToken);
        return TopicDto.From(topic, subscriptions);
    }
}
