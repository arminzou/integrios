using Integrios.Application.Auth;
using Integrios.Domain.Tenants;

namespace Integrios.Admin.Auth;

public sealed record AdminPrincipal : IPrincipalContext
{
    public required AdminKey AdminKey { get; init; }

    public Guid? TenantId => AdminKey.TenantId;
    public bool IsGlobal => AdminKey.IsGlobal;
}

public static class HttpContextAdminExtensions
{
    private static readonly object Key = new();

    public static AdminPrincipal GetAdminPrincipal(this HttpContext context)
        => context.Items.TryGetValue(Key, out var value) && value is AdminPrincipal ap
            ? ap
            : throw new InvalidOperationException(
                "AdminPrincipal is not available. Endpoint must be behind the AdminKey scheme.");

    internal static void SetAdminPrincipal(this HttpContext context, AdminPrincipal principal)
        => context.Items[Key] = principal;
}
