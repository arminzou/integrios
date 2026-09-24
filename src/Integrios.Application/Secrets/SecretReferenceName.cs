using Integrios.Domain.ValueObjects;

namespace Integrios.Application.Secrets;

/// A secret reference shares the Tenant slug grammar, so every configuration source, including
/// ones whose names allow only letters, digits, and hyphens, can store it under its own name.
/// Consecutive hyphens are excluded because Azure Key Vault's key mapping reads "--" as a section
/// separator.
public static class SecretReferenceName
{
    public static bool IsValid(string? value) =>
        TenantSlug.IsValid(value) && !value!.Contains("--", StringComparison.Ordinal);
}
