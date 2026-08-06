using Integrios.Domain.Topics;
using System.Text.Json;

namespace Integrios.Application.Subscriptions;

public sealed record SubscriptionDto(
    Guid Id,
    Guid TopicId,
    Guid TenantId,
    string Name,
    JsonElement MatchRules,
    Guid DestinationConnectionId,
    JsonElement? TransformConfig,
    HttpDeliveryConfiguration HttpDelivery,
    string Status,
    int OrderIndex,
    string? Description,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public static SubscriptionDto From(Subscription subscription) => new(
        subscription.Id,
        subscription.TopicId,
        subscription.TenantId,
        subscription.Name,
        subscription.MatchRules,
        subscription.DestinationConnectionId,
        subscription.TransformConfig,
        subscription.HttpDelivery,
        subscription.Status.ToString().ToLowerInvariant(),
        subscription.OrderIndex,
        subscription.Description,
        subscription.CreatedAt,
        subscription.UpdatedAt);
}
