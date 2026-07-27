using System.Diagnostics.CodeAnalysis;

namespace Integrios.Application.Delivery;

public static class OutboundHttpDestination
{
    public static bool TryParse(string? value, [NotNullWhen(true)] out Uri? destination)
    {
        destination = null;

        if (string.IsNullOrWhiteSpace(value)
            || !Uri.TryCreate(value, UriKind.Absolute, out Uri? parsed)
            || !parsed.IsWellFormedOriginalString()
            || string.IsNullOrWhiteSpace(parsed.Host)
            || (!parsed.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                && !parsed.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        destination = parsed;
        return true;
    }
}
