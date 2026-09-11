using System.Text.Json;
using Integrios.Domain.Entities;
using Integrios.Domain.ValueObjects;

namespace Integrios.Application.Authoring.Subscriptions;

public sealed record SubscriptionDto(
    Guid Id,
    Guid TopicId,
    Guid TenantId,
    string Name,
    JsonElement MatchRules,
    Guid DestinationId,
    JsonElement? MappingConfig,
    HttpDeliveryConfiguration HttpDelivery,
    HttpSuccessRule? HttpSuccess,
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
        subscription.DestinationId,
        subscription.MappingConfig,
        subscription.HttpDelivery,
        subscription.HttpSuccess,
        subscription.Status.ToString().ToLowerInvariant(),
        subscription.OrderIndex,
        subscription.Description,
        subscription.CreatedAt,
        subscription.UpdatedAt);
}
