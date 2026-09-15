using System.Text.RegularExpressions;

namespace Integrios.Application.Common;

// The field-name grammar an HTTP header has to satisfy in either direction: one Integrios sends with
// a Delivery, and one a Source names to read an Event identity out of a provider request. The
// grammar is the RFC 9110 token; which names are additionally reserved is directional, and belongs
// with the direction that reserves them.
public static partial class HttpHeaderName
{
    public static bool IsValid(string? name) =>
        !string.IsNullOrEmpty(name)
        && name.Length <= 128
        && Pattern().IsMatch(name);

    [GeneratedRegex(@"^[!#$%&'*+\-.^_`|~0-9A-Za-z]+$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}
