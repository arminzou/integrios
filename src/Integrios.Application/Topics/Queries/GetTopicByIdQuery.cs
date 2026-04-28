using Integrios.Application.Abstractions;
using MediatR;

namespace Integrios.Application.Topics;

public sealed record GetTopicByIdQuery(Guid TenantId, Guid Id) : IRequest<TopicResponse?>;

internal sealed class GetTopicByIdQueryHandler(ITopicRepository topicRepository)
    : IRequestHandler<GetTopicByIdQuery, TopicResponse?>
{
    public async Task<TopicResponse?> Handle(GetTopicByIdQuery query, CancellationToken cancellationToken)
    {
        var topic = await topicRepository.GetByIdAsync(query.TenantId, query.Id, cancellationToken);
        return topic is null ? null : TopicResponse.From(topic);
    }
}
