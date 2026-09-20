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
    DateTimeOffset? LastUsedAt,
    DateTimeOffset? RevokedAt)
{
    public static TenantApiKeyListItemDto From(TenantApiKey key) => new(
        key.Id,
        key.TenantId,
        key.Name,
        key.KeyPrefix,
        TenantApiKeyState.From(key),
        key.Description,
        key.CreatedAt,
        key.LastUsedAt,
        key.RevokedAt);
}
