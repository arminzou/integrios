using MediatR;

namespace Integrios.Application.Subscriptions;

public sealed record GetSubscriptionByIdQuery(Guid TenantId, Guid TopicId, Guid Id) : IRequest<SubscriptionDto?>;

internal sealed class GetSubscriptionByIdQueryHandler(ISubscriptionRepository subscriptionRepository)
    : IRequestHandler<GetSubscriptionByIdQuery, SubscriptionDto?>
{
    public async Task<SubscriptionDto?> Handle(GetSubscriptionByIdQuery query, CancellationToken cancellationToken)
    {
        var subscription = await subscriptionRepository.GetByIdAsync(query.TenantId, query.TopicId, query.Id, cancellationToken);
        return subscription is null ? null : SubscriptionDto.From(subscription);
    }
}
