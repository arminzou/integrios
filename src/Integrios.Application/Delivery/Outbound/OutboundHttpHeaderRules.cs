namespace Integrios.Application.Delivery;

public static class OutboundHttpHeaderRules
{
    private static readonly HashSet<string> TransportOrPlatformOwnedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection",
        "Expect",
        "Host",
        "Keep-Alive",
        "Proxy-Connection",
        "TE",
        "Trailer",
        "Transfer-Encoding",
        "Upgrade",
        "Integrios-Event-Id",
        "Integrios-Delivery-Id",
        "Integrios-Attempt-Id",
        "Integrios-Attempt-Number"
    };

    public static bool IsTransportOrPlatformOwned(string name) =>
        name.StartsWith("Content-", StringComparison.OrdinalIgnoreCase)
        || TransportOrPlatformOwnedHeaders.Contains(name);

    public static bool IsReservedForStaticConfiguration(string name) =>
        name.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
        || IsTransportOrPlatformOwned(name);
}
