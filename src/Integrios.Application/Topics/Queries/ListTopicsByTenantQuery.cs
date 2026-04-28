using Integrios.Application.Abstractions;
using MediatR;

namespace Integrios.Application.Topics;

public sealed record ListTopicsByTenantQuery(Guid TenantId, string? AfterCursor, int Limit) : IRequest<TopicListResponse>;

internal sealed class ListTopicsByTenantQueryHandler(ITopicRepository topicRepository)
    : IRequestHandler<ListTopicsByTenantQuery, TopicListResponse>
{
    public async Task<TopicListResponse> Handle(ListTopicsByTenantQuery query, CancellationToken cancellationToken)
    {
        var (items, nextCursor) = await topicRepository.ListByTenantAsync(
            query.TenantId, query.AfterCursor, query.Limit, cancellationToken);
        return new TopicListResponse(items.Select(TopicResponse.From).ToList(), nextCursor);
    }
}
