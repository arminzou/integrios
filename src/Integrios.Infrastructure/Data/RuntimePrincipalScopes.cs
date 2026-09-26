namespace Integrios.Infrastructure.Data;

internal static class RuntimePrincipalScopes
{
    public static readonly string[] OperatorAuthenticationTables =
    [
        "operator_keys",
        "operator_identities",
        "password_credentials",
        "users",
    ];
}
