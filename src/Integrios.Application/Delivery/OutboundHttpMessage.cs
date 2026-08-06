namespace Integrios.Application.Delivery;

// ToString redacts the URI because a Subscription request target may carry an operation-specific
// query string, and header values may carry a resolved secret.
public sealed record OutboundHttpMessage(
    string Method,
    string Uri,
    IReadOnlyDictionary<string, string> Headers,
    string? JsonBody)
{
    public override string ToString() =>
        $"{Method} <redacted> ({Headers.Count} headers, body={(JsonBody is null ? "none" : "json")})";
}
