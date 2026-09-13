namespace Integrios.Domain.ValueObjects;

/// A Tenant's key. It keeps its own name because it is also the Operator-facing secret namespace,
/// where it appears as a path segment; the grammar itself is the one every key shares.
public static class TenantSlug
{
    public const string Pattern = ResourceKey.Pattern;

    public static bool IsValid(string? value) => ResourceKey.IsValid(value);
}
