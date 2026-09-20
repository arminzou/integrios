using Integrios.Domain.Entities;

namespace Integrios.Application.Authoring.TenantApiKeys;

/// <summary>
/// The state a TenantApiKey is in as an Operator reads it, derived rather than stored. Revocation is
/// recorded as its own instant, <see cref="TenantApiKey.RevokedAt"/>, and is terminal.
/// </summary>
public static class TenantApiKeyState
{
    public static string From(TenantApiKey key) => key.RevokedAt is null ? "active" : "revoked";
}
