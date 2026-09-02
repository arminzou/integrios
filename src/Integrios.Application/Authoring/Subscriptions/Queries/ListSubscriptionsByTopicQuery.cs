using MediatR;
using Integrios.Domain.Enums;

namespace Integrios.Application.Authoring.Subscriptions;

public sealed record ListSubscriptionsByTopicQuery(Guid TenantId, Guid TopicId, OperationalStatus? Status, string? AfterCursor, int Limit) : IRequest<SubscriptionListDto>;

internal sealed class ListSubscriptionsByTopicQueryHandler(ISubscriptionRepository subscriptionRepository)
    : IRequestHandler<ListSubscriptionsByTopicQuery, SubscriptionListDto>
{
    public async Task<SubscriptionListDto> Handle(ListSubscriptionsByTopicQuery query, CancellationToken cancellationToken)
    {
        var (items, nextCursor) = await subscriptionRepository.ListByTopicAsync(
            query.TenantId,
            query.TopicId,
            query.Status,
            query.AfterCursor,
            query.Limit,
            cancellationToken);

        return new SubscriptionListDto(items.Select(SubscriptionListItemDto.From).ToList(), nextCursor);
    }
}
