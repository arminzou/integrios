using Integrios.Domain.Entities;
using Integrios.Domain.Enums;

namespace Integrios.Application.Authoring.Topics;

public sealed record TopicDto(
    Guid Id,
    Guid TenantId,
    string Key,
    string Name,
    string Status,
    string? Description,
    // How many Subscriptions match this Topic. Nothing else on the Topic says whether anything is
    // listening to it, and a Topic nothing subscribes to is the established signal for a missing
    // Subscription rather than an empty section.
    int SubscriptionCount,
    // What the Topic's Sources declare, Disabled ones included, so Subscriptions can be authored
    // before intake is enabled. Computed on read; a Topic stores no Event types of its own.
    IReadOnlyList<string> EventTypes,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public static TopicDto From(Topic t, int subscriptionCount, IReadOnlyList<string> eventTypes) => new(
        t.Id,
        t.TenantId,
        t.Key,
        t.Name,
        t.Status.ToString().ToLowerInvariant(),
        t.Description,
        subscriptionCount,
        eventTypes,
        t.CreatedAt,
        t.UpdatedAt);
}
