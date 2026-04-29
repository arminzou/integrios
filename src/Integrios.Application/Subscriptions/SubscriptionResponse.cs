using Integrios.Domain.Topics;
using System.Text.Json;

namespace Integrios.Application.Subscriptions;

public sealed record SubscriptionResponse(
    Guid Id,
    Guid TopicId,
    Guid TenantId,
    string Name,
    JsonElement MatchRules,
    Guid DestinationConnectionId,
    bool DlqEnabled,
    string Status,
    int OrderIndex,
    string? Description,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public static SubscriptionResponse From(Subscription subscription) => new(
        subscription.Id,
        subscription.TopicId,
        subscription.TenantId,
        subscription.Name,
        subscription.MatchRules,
        subscription.DestinationConnectionId,
        subscription.DlqEnabled,
        subscription.Status.ToString().ToLowerInvariant(),
        subscription.OrderIndex,
        subscription.Description,
        subscription.CreatedAt,
        subscription.UpdatedAt);
}

public sealed record SubscriptionListResponse(IReadOnlyList<SubscriptionResponse> Items, string? NextCursor);
