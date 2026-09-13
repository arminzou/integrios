using MediatR;

namespace Integrios.Application.Authoring.Topics;

public sealed record ListTopicsByTenantQuery(Guid TenantId, TopicListFilter Filter, string? AfterCursor, int Limit) : IRequest<TopicListDto>;

internal sealed class ListTopicsByTenantQueryHandler(ITopicRepository topicRepository)
    : IRequestHandler<ListTopicsByTenantQuery, TopicListDto>
{
    public async Task<TopicListDto> Handle(ListTopicsByTenantQuery query, CancellationToken cancellationToken)
    {
        var (items, nextCursor) = await topicRepository.ListByTenantAsync(
            query.TenantId, query.Filter, query.AfterCursor, query.Limit, cancellationToken);
        return new TopicListDto(
            items.Select(row => TopicDto.From(row.Topic, row.SubscriptionCount)).ToList(),
            nextCursor);
    }
}
