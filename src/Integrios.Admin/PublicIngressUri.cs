namespace Integrios.Admin;

internal sealed record PublicIngressUri(Uri Value)
{
    private const string ConfigurationKey = "Integrios:PublicIngressBaseUri";

    public static PublicIngressUri FromConfiguration(
        IConfiguration configuration,
        IHostEnvironment environment) =>
        Parse(configuration[ConfigurationKey], environment.IsDevelopment());

    internal static PublicIngressUri Parse(string? configuredValue, bool allowHttp)
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

        return new PublicIngressUri(uri);
    }

    public string AppendCallbackPath(string callbackPath)
    {
        if (!callbackPath.StartsWith("/", StringComparison.Ordinal))
            throw new ArgumentException("A callback path must start with '/'.", nameof(callbackPath));

        return Value.AbsoluteUri.TrimEnd('/') + callbackPath;
    }
}
