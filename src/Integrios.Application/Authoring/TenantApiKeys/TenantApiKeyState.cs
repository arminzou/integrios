using Integrios.Domain.Entities;

namespace Integrios.Application.Authoring.TenantApiKeys;

/// <summary>
/// The state a TenantApiKey is in as an Operator reads it, derived rather than stored.
/// </summary>
/// <remarks>
/// Revocation is recorded as its own instant and a revoked key is excluded from every authoring read
/// and list, so it never reaches this derivation. Expiry has no status of its own, so it is derived
/// from <see cref="TenantApiKey.ExpiresAt"/>.
/// </remarks>
public static class TenantApiKeyState
{
    public static string From(TenantApiKey key, DateTimeOffset now) =>
        key.ExpiresAt is not null && key.ExpiresAt <= now ? "expired" : "active";
}
