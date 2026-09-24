using Integrios.Domain.ValueObjects;

namespace Integrios.Application.Secrets;

/// A secret reference shares the Tenant slug grammar, so every configuration source, including
/// ones whose names allow only letters, digits, and hyphens, can store it under its own name.
public static class SecretReferenceName
{
    public static bool IsValid(string? value) => TenantSlug.IsValid(value);
}
