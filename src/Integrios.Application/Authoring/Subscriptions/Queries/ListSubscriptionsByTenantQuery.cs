using MediatR;

namespace Integrios.Application.Authoring.Subscriptions;

public sealed record ListSubscriptionsByTenantQuery(
    Guid TenantId,
    SubscriptionListFilter Filter,
    string? AfterCursor,
    int Limit) : IRequest<SubscriptionByTenantListDto>;

internal sealed class ListSubscriptionsByTenantQueryHandler(ISubscriptionQueries subscriptionQueries)
    : IRequestHandler<ListSubscriptionsByTenantQuery, SubscriptionByTenantListDto>
{
    public Task<SubscriptionByTenantListDto> Handle(
        ListSubscriptionsByTenantQuery query,
        CancellationToken cancellationToken) =>
        subscriptionQueries.ListByTenantAsync(
            query.TenantId,
            query.Filter,
            query.AfterCursor,
            query.Limit,
            cancellationToken);
}
