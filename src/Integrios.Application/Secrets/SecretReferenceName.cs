using System.Text.RegularExpressions;

namespace Integrios.Application.Secrets;

public static partial class SecretReferenceName
{
    public const string Pattern = "^[a-z0-9](?:[a-z0-9_]{0,62})$";

    public static bool IsValid(string? value) =>
        value is not null && ReferencePattern().IsMatch(value);

    [GeneratedRegex(Pattern, RegexOptions.CultureInvariant)]
    private static partial Regex ReferencePattern();
}
