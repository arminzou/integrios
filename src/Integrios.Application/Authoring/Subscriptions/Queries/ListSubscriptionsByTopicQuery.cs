using MediatR;

namespace Integrios.Application.Authoring.Subscriptions;

public sealed record ListSubscriptionsByTopicQuery(Guid TenantId, Guid TopicId, string? AfterCursor, int Limit) : IRequest<SubscriptionListDto>;

internal sealed class ListSubscriptionsByTopicQueryHandler(ISubscriptionRepository subscriptionRepository)
    : IRequestHandler<ListSubscriptionsByTopicQuery, SubscriptionListDto>
{
    public async Task<SubscriptionListDto> Handle(ListSubscriptionsByTopicQuery query, CancellationToken cancellationToken)
    {
        var (items, nextCursor) = await subscriptionRepository.ListByTopicAsync(
            query.TenantId,
            query.TopicId,
            query.AfterCursor,
            query.Limit,
            cancellationToken);

        return new SubscriptionListDto(items.Select(SubscriptionDto.From).ToList(), nextCursor);
    }
}
