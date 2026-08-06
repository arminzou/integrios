using System.Text;

namespace Integrios.Application.Delivery;

public static class HttpTargetComposer
{
    private const int MaxTargetBytes = 8 * 1024;

    public static string? ValidateRelativeTarget(string? target)
    {
        if (string.IsNullOrEmpty(target))
            return null;

        if (Encoding.UTF8.GetByteCount(target) > MaxTargetBytes)
            return "http_delivery.path must not exceed 8 KiB of UTF-8 text.";

        if (target.Contains('\\'))
            return "http_delivery.path must not contain backslashes.";

        if (target.Contains('#'))
            return "http_delivery.path must not contain a fragment.";

        if (target.StartsWith("//", StringComparison.Ordinal))
            return "http_delivery.path must not be scheme-relative.";

        if (Uri.TryCreate(target, UriKind.Absolute, out _))
            return "http_delivery.path must be relative.";

        string path = target.Split('?', 2)[0].TrimStart('/');
        foreach (string segment in path.Split('/', StringSplitOptions.None))
        {
            // Decoded twice: a proxy that unescapes once before forwarding turns %252e%252e into a
            // real traversal segment. Uri.UnescapeDataString leaves malformed sequences intact
            // rather than throwing, so a stray '%' needs no separate branch.
            string decoded = Uri.UnescapeDataString(Uri.UnescapeDataString(segment));
            if (decoded is "." or ".." || decoded.Contains('/') || decoded.Contains('\\'))
                return "http_delivery.path must not contain traversal segments or encoded path separators.";
        }

        return null;
    }

    public static string Compose(string baseUri, string? relativeTarget)
    {
        if (!OutboundHttpDestination.TryParse(baseUri, out Uri? parsedBase)
            || !string.IsNullOrEmpty(parsedBase.Query)
            || !string.IsNullOrEmpty(parsedBase.Fragment))
        {
            throw new DeliveryConfigurationException(
                "Destination base_uri must be an absolute HTTP or HTTPS URI without a query or fragment.");
        }

        string? targetError = ValidateRelativeTarget(relativeTarget);
        if (targetError is not null)
            throw new DeliveryConfigurationException(targetError);

        if (string.IsNullOrEmpty(relativeTarget))
            return baseUri;

        if (relativeTarget[0] == '?')
            return baseUri + relativeTarget;

        return $"{baseUri.TrimEnd('/')}/{relativeTarget.TrimStart('/')}";
    }
}
