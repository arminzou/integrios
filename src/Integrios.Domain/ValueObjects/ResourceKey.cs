using System.Text.RegularExpressions;

namespace Integrios.Domain.ValueObjects;

/// The grammar every key shares: a lowercase DNS label. A key has no uppercase form, so PostgreSQL
/// and SQL Server compare keys identically whatever collation the server was created with.
public static partial class ResourceKey
{
    public const string Pattern = "^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$";

    public static bool IsValid(string? value) =>
        value is not null && KeyPattern().IsMatch(value);

    [GeneratedRegex(Pattern, RegexOptions.CultureInvariant)]
    private static partial Regex KeyPattern();
}
