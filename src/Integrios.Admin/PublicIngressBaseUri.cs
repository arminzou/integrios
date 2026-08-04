namespace Integrios.Admin;

internal sealed record PublicIngressBaseUri
{
    internal const string ConfigurationKey = "Integrios:PublicIngressBaseUri";

    public Uri Value { get; }

    private PublicIngressBaseUri(Uri value)
    {
        Value = value;
    }

    internal static PublicIngressBaseUri Parse(string? configuredValue, bool allowHttp)
    {
        if (string.IsNullOrWhiteSpace(configuredValue))
            throw new InvalidOperationException($"{ConfigurationKey} is required.");

        if (!Uri.TryCreate(configuredValue, UriKind.Absolute, out Uri? uri)
            || uri.Scheme is not ("http" or "https")
            || string.IsNullOrWhiteSpace(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new InvalidOperationException(
                $"{ConfigurationKey} must be an absolute HTTP(S) URI with no user info, query, or fragment.");
        }

        if (!allowHttp && uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException($"{ConfigurationKey} must use HTTPS outside Development.");

        return new PublicIngressBaseUri(uri);
    }

    public string AppendCallbackPath(string callbackPath)
    {
        if (!callbackPath.StartsWith('/'))
            throw new ArgumentException("A callback path must start with '/'.", nameof(callbackPath));

        return Value.AbsoluteUri.TrimEnd('/') + callbackPath;
    }
}
