using Integrios.Domain.Entities;
using Integrios.Domain.Enums;

namespace Integrios.Application.Authoring.TenantApiKeys;

/// <summary>
/// The state a TenantApiKey is in as an Operator reads it, derived rather than stored.
/// </summary>
/// <remarks>
/// Revocation and expiry are recorded as their own instants rather than as status values, so
/// neither is answerable from <see cref="TenantApiKey.Status"/> alone. Shared by the list and the
/// single-key read: they described the same key differently while each derived its own answer —
/// the list said "revoked" where the detail said "disabled" — and the dashboard could not tell a
/// revoked key from a merely disabled one, so it offered to revoke one that already was.
/// </remarks>
public static class TenantApiKeyState
{
    public static string From(TenantApiKey key, DateTimeOffset now) => key.RevokedAt is not null
        ? "revoked"
        : key.Status == OperationalStatus.Active && key.ExpiresAt is not null && key.ExpiresAt <= now
            ? "expired"
            : key.Status.ToString().ToLowerInvariant();
}
