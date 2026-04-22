using Integrios.Core.Domain.Tenants;

namespace Integrios.Api.Auth;

public sealed record TenantContext
{
    public required Tenant Tenant { get; init; }
    public required ApiKey ApiKey { get; init; }
}

public static class HttpContextTenantExtensions
{
    private static readonly object Key = new();

    public static TenantContext GetTenantContext(this HttpContext context)
        => context.Items.TryGetValue(Key, out var value) && value is TenantContext tc
            ? tc
            : throw new InvalidOperationException(
                "TenantContext is not available. Endpoint must be behind the API key filter.");

    internal static void SetTenantContext(this HttpContext context, TenantContext tenantContext)
        => context.Items[Key] = tenantContext;
}
