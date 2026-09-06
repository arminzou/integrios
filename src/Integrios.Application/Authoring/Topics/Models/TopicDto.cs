using Integrios.Domain.Entities;
using Integrios.Domain.Enums;

namespace Integrios.Application.Authoring.Topics;

public sealed record TopicDto(
    Guid Id,
    Guid TenantId,
    string Name,
    string Status,
    string? Description,
    // How many Subscriptions match this Topic. Nothing else on the Topic says whether anything is
    // listening to it, and a Topic nothing subscribes to is the established signal for a missing
    // Subscription rather than an empty section.
    int SubscriptionCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public static TopicDto From(Topic t, int subscriptionCount) => new(
        t.Id,
        t.TenantId,
        t.Name,
        t.Status.ToString().ToLowerInvariant(),
        t.Description,
        subscriptionCount,
        t.CreatedAt,
        t.UpdatedAt);
}
