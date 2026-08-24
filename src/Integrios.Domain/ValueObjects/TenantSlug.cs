using System.Text.RegularExpressions;

namespace Integrios.Domain.ValueObjects;

public static partial class TenantSlug
{
    public const string Pattern = "^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$";

    public static bool IsValid(string? value) =>
        value is not null && SlugPattern().IsMatch(value);

    [GeneratedRegex(Pattern, RegexOptions.CultureInvariant)]
    private static partial Regex SlugPattern();
}
