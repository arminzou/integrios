using Integrios.Domain.Entities;
using Integrios.Domain.Enums;

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
        StateFrom(key, now),
        key.Description,
        key.CreatedAt,
        key.ExpiresAt,
        key.LastUsedAt);

    private static string StateFrom(TenantApiKey key, DateTimeOffset now) => key.RevokedAt is not null
        ? "revoked"
        : key.Status == OperationalStatus.Active && key.ExpiresAt is not null && key.ExpiresAt <= now
            ? "expired"
            : "active";
}
