using MediatR;
using Integrios.Domain.Enums;

namespace Integrios.Application.Authoring.Subscriptions;

public sealed record ListSubscriptionsByTopicQuery(Guid TenantId, Guid TopicId, OperationalStatus? Status, string? AfterCursor, int Limit) : IRequest<SubscriptionListDto>;

internal sealed class ListSubscriptionsByTopicQueryHandler(ISubscriptionQueries subscriptionQueries)
    : IRequestHandler<ListSubscriptionsByTopicQuery, SubscriptionListDto>
{
    public Task<SubscriptionListDto> Handle(ListSubscriptionsByTopicQuery query, CancellationToken cancellationToken) =>
        subscriptionQueries.ListByTopicAsync(
            query.TenantId,
            query.TopicId,
            query.Status,
            query.AfterCursor,
            query.Limit,
            cancellationToken);
}
