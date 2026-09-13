using Integrios.Domain.Entities;

namespace Integrios.Application.Authoring.TenantApiKeys;

public sealed record TenantApiKeyListItemDto(
    Guid Id,
    Guid TenantId,
    string Name,
    string KeyPrefix,
    string State,
    string? Description,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? LastUsedAt)
{
    public static TenantApiKeyListItemDto From(TenantApiKey key, DateTimeOffset now) => new(
        key.Id,
        key.TenantId,
        key.Name,
        key.KeyPrefix,
        TenantApiKeyState.From(key, now),
        key.Description,
        key.CreatedAt,
        key.ExpiresAt,
        key.LastUsedAt);
}
