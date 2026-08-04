using MediatR;

namespace Integrios.Application.Topics;

public sealed record ListTopicsByTenantQuery(Guid TenantId, string? AfterCursor, int Limit) : IRequest<TopicListDto>;

internal sealed class ListTopicsByTenantQueryHandler(ITopicRepository topicRepository)
    : IRequestHandler<ListTopicsByTenantQuery, TopicListDto>
{
    public async Task<TopicListDto> Handle(ListTopicsByTenantQuery query, CancellationToken cancellationToken)
    {
        var (items, nextCursor) = await topicRepository.ListByTenantAsync(
            query.TenantId, query.AfterCursor, query.Limit, cancellationToken);
        return new TopicListDto(items.Select(TopicDto.From).ToList(), nextCursor);
    }
}
