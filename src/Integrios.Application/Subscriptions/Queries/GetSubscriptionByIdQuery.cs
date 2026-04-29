using Integrios.Application.Abstractions;
using MediatR;

namespace Integrios.Application.Subscriptions;

public sealed record GetSubscriptionByIdQuery(Guid TenantId, Guid TopicId, Guid Id) : IRequest<SubscriptionResponse?>;

internal sealed class GetSubscriptionByIdQueryHandler(ISubscriptionRepository subscriptionRepository)
    : IRequestHandler<GetSubscriptionByIdQuery, SubscriptionResponse?>
{
    public async Task<SubscriptionResponse?> Handle(GetSubscriptionByIdQuery query, CancellationToken cancellationToken)
    {
        var subscription = await subscriptionRepository.GetByIdAsync(query.TenantId, query.TopicId, query.Id, cancellationToken);
        return subscription is null ? null : SubscriptionResponse.From(subscription);
    }
}
