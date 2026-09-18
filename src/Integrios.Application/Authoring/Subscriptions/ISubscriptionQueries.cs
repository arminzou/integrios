using Integrios.Domain.Enums;

namespace Integrios.Application.Authoring.Subscriptions;

public interface ISubscriptionQueries
{
    Task<SubscriptionListDto> ListByTopicAsync(
        Guid tenantId,
        Guid topicId,
        EnablementStatus? status,
        string? afterCursor,
        int limit,
        CancellationToken cancellationToken);

    Task<SubscriptionByTenantListDto> ListByTenantAsync(
        Guid tenantId,
        SubscriptionListFilter filter,
        string? afterCursor,
        int limit,
        CancellationToken cancellationToken);
}
