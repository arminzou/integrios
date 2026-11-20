using Integrios.Domain.Entities;

namespace Integrios.Application.Authoring.SubscriberKeys;

public sealed record SubscriberKeyListItemDto(
    Guid Id,
    Guid TenantId, Guid DestinationId,
    string Name,
    string KeyPrefix,
    string State,
    string? Description,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastUsedAt,
    DateTimeOffset? RevokedAt)
{
    public static SubscriberKeyListItemDto From(SubscriberKey key) => new(
        key.Id,
        key.TenantId,
        key.DestinationId,
        key.Name,
        key.KeyPrefix,
        SubscriberKeyState.From(key),
        key.Description,
        key.CreatedAt,
        key.LastUsedAt,
        key.RevokedAt);
}
