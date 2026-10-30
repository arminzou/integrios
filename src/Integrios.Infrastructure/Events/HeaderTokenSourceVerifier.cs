using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Integrios.Application.Ingestion;

namespace Integrios.Infrastructure.Events;

internal sealed class HeaderTokenSourceVerifier : ISourceVerifier
{
    private const string DefaultHeaderName = "X-Integrios-Token";

    public string Scheme => "header_token";
    public IReadOnlyList<string> RequiredConfigFields => [];
    public IReadOnlyList<string> RequiredSecretFields => ["token"];

    public bool Verify(
        ReadOnlyMemory<byte> rawBody,
        IReadOnlyDictionary<string, string> headers,
        JsonElement config,
        IReadOnlyDictionary<string, string> secrets)
    {
        string headerName = HeaderName(config);
        string prefix = config.TryGetProperty("prefix", out JsonElement prefixElement)
            ? prefixElement.GetString() ?? ""
            : "";
        if (!secrets.TryGetValue("token", out string? token))
            throw new SourceVerificationException("Source verification secret field 'token' is required.");

        if (!headers.TryGetValue(headerName, out string? headerValue)
            || !headerValue.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        // FixedTimeEquals returns false for unequal lengths without comparing, so only the token's
        // length can leak, never its content.
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(headerValue[prefix.Length..]),
            Encoding.UTF8.GetBytes(token));
    }

    public IReadOnlyList<string> CredentialHeaders(JsonElement config) => [HeaderName(config)];

    private static string HeaderName(JsonElement config) =>
        config.TryGetProperty("header_name", out JsonElement headerNameElement)
            ? headerNameElement.GetString() ?? DefaultHeaderName
            : DefaultHeaderName;
}
