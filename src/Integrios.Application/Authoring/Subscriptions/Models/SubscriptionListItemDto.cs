using Integrios.Domain.Entities;

namespace Integrios.Application.Authoring.Subscriptions;

public sealed record SubscriptionListItemDto(
    Guid Id,
    Guid TopicId,
    Guid TenantId,
    string Name,
    Guid DestinationConnectionId,
    string Status,
    int OrderIndex,
    string? Description,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public static SubscriptionListItemDto From(Subscription subscription) => new(
        subscription.Id,
        subscription.TopicId,
        subscription.TenantId,
        subscription.Name,
        subscription.DestinationConnectionId,
        subscription.Status.ToString().ToLowerInvariant(),
        subscription.OrderIndex,
        subscription.Description,
        subscription.CreatedAt,
        subscription.UpdatedAt);
}
