using Microsoft.Extensions.Configuration;

namespace Integrios.Infrastructure.Data;

internal enum RuntimePrincipalScope
{
    ControlPlane,
    DataPlane,
}

internal sealed record RuntimePrincipal(
    string Name,
    RuntimePrincipalScope Scope,
    string? Password,
    Guid? EntraClientId,
    Guid? EntraObjectId);

internal static class RuntimePrincipalConfiguration
{
    private const string PrincipalsKey = "Database:RuntimePrincipals";

    public static IReadOnlyList<RuntimePrincipal> Read(IConfiguration configuration, DatabaseProvider provider)
    {
        IConfigurationSection section = configuration.GetSection(PrincipalsKey);
        IConfigurationSection[] entries = section.GetChildren().ToArray();
        if (entries.Length == 0)
            throw new InvalidOperationException($"{PrincipalsKey} must contain at least one principal.");

        var principals = new List<RuntimePrincipal>(entries.Length);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (IConfigurationSection entry in entries)
        {
            string key = $"{PrincipalsKey}:{entry.Key}";
            string name = Required(configuration, key + ":Name");
            if (!IsValidName(name))
                throw new InvalidOperationException($"{key}:Name must contain 1 to 63 ASCII letters, digits, '_' or '-'.");
            if (!names.Add(name))
                throw new InvalidOperationException($"{key}:Name duplicates another configured principal.");

            string scopeValue = Required(configuration, key + ":Scope");
            RuntimePrincipalScope scope = scopeValue switch
            {
                "control-plane" => RuntimePrincipalScope.ControlPlane,
                "data-plane" => RuntimePrincipalScope.DataPlane,
                _ => throw new InvalidOperationException($"{key}:Scope must be 'control-plane' or 'data-plane'."),
            };

            string? password = OptionalCredential(configuration, key + ":Password", out bool hasPassword);
            Guid? clientId = OptionalGuid(configuration, key + ":EntraClientId", out bool hasClientId);
            Guid? objectId = OptionalGuid(configuration, key + ":EntraObjectId", out bool hasObjectId);

            bool hasEntra = hasClientId || hasObjectId;
            if (hasPassword && hasEntra)
                throw new InvalidOperationException($"{key}:Password cannot be combined with EntraClientId or EntraObjectId.");
            // SQL Server caps passwords at 128 characters, and QUOTENAME returns NULL past that,
            // which would turn a password update into a silent no-op.
            if (provider == DatabaseProvider.SqlServer && password?.Length > 128)
                throw new InvalidOperationException($"{key}:Password must be at most 128 characters for SQL Server.");

            if (hasEntra)
            {
                string requiredEntraKey = provider == DatabaseProvider.SqlServer
                    ? key + ":EntraClientId"
                    : key + ":EntraObjectId";
                bool hasRequiredEntraId = provider == DatabaseProvider.SqlServer ? hasClientId : hasObjectId;
                if (!hasRequiredEntraId)
                    throw new InvalidOperationException($"{requiredEntraKey} is required for the selected database provider.");
            }

            principals.Add(new RuntimePrincipal(name, scope, password, clientId, objectId));
        }

        return principals;
    }

    private static string Required(IConfiguration configuration, string key)
    {
        string? value = configuration[key];
        return string.IsNullOrEmpty(value)
            ? throw new InvalidOperationException($"{key} is required and must not be empty.")
            : value;
    }

    private static string? OptionalCredential(IConfiguration configuration, string key, out bool isPresent)
    {
        isPresent = HasKey(configuration, key);
        if (!isPresent)
            return null;

        string? value = configuration[key];
        return string.IsNullOrEmpty(value)
            ? throw new InvalidOperationException($"{key} is present but empty.")
            : value;
    }

    private static Guid? OptionalGuid(IConfiguration configuration, string key, out bool isPresent)
    {
        string? value = OptionalCredential(configuration, key, out isPresent);
        if (!isPresent)
            return null;
        return Guid.TryParse(value, out Guid parsed)
            ? parsed
            : throw new InvalidOperationException($"{key} must be a GUID.");
    }

    private static bool HasKey(IConfiguration configuration, string key) =>
        configuration.AsEnumerable().Any(pair => string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase));

    private static bool IsValidName(string name) =>
        name.Length is > 0 and <= 63
        && name.All(character =>
            character is >= 'A' and <= 'Z'
            or >= 'a' and <= 'z'
            or >= '0' and <= '9'
            or '_'
            or '-');
}
