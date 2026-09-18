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
        ILookup<Guid, SourceDeclaration> declarations = (await topicRepository.ListSourceDeclarationsAsync(
                query.TenantId, items.Select(row => row.Topic.Id).ToList(), cancellationToken))
            .ToLookup(declaration => declaration.TopicId);
        return new TopicListDto(
            items.Select(row => TopicDto.From(
                row.Topic, row.SubscriptionCount, TopicEventTypes.Union(declarations[row.Topic.Id]))).ToList(),
            nextCursor);
    }
}
