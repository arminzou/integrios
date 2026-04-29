using Integrios.Application.Abstractions;
using MediatR;

namespace Integrios.Application.Subscriptions;

public sealed record ListSubscriptionsByTopicQuery(Guid TenantId, Guid TopicId, string? AfterCursor, int Limit) : IRequest<SubscriptionListResponse>;

internal sealed class ListSubscriptionsByTopicQueryHandler(ISubscriptionRepository subscriptionRepository)
    : IRequestHandler<ListSubscriptionsByTopicQuery, SubscriptionListResponse>
{
    public async Task<SubscriptionListResponse> Handle(ListSubscriptionsByTopicQuery query, CancellationToken cancellationToken)
    {
        var (items, nextCursor) = await subscriptionRepository.ListByTopicAsync(
            query.TenantId,
            query.TopicId,
            query.AfterCursor,
            query.Limit,
            cancellationToken);

        return new SubscriptionListResponse(items.Select(SubscriptionResponse.From).ToList(), nextCursor);
    }
}
