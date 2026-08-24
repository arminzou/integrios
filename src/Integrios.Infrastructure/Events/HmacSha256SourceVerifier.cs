using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Integrios.Application.Ingestion;

namespace Integrios.Infrastructure.Events;

// hmac_sha256 is a fixed platform verification contract (ConnectorManifestParser.ValidatePlatformSchemes
// requires every Connector to declare it with no required config and exactly the "secret" secret
// ref), not a per-provider-configurable scheme. header_name/prefix/encoding default to GitHub's
// shape (the only provider this verified before this generalization) but remain overridable via
// optional, non-required Config keys for a Connector whose provider uses a differently-shaped
// HMAC-SHA256 signature header.
internal sealed class HmacSha256SourceVerifier : ISourceVerifier
{
    private const string DefaultHeaderName = "X-Hub-Signature-256";
    private const string DefaultPrefix = "sha256=";
    private const string DefaultEncoding = "hex";

    public string Scheme => "hmac_sha256";
    public IReadOnlyList<string> RequiredConfigFields => [];
    public IReadOnlyList<string> RequiredSecretFields => ["secret"];

    public bool Verify(
        ReadOnlyMemory<byte> rawBody,
        IReadOnlyDictionary<string, string> headers,
        JsonElement config,
        IReadOnlyDictionary<string, string> secrets)
    {
        string headerName = config.TryGetProperty("header_name", out JsonElement headerNameElement)
            ? headerNameElement.GetString() ?? DefaultHeaderName
            : DefaultHeaderName;
        string prefix = config.TryGetProperty("prefix", out JsonElement prefixElement)
            ? prefixElement.GetString() ?? DefaultPrefix
            : DefaultPrefix;
        string encoding = config.TryGetProperty("encoding", out JsonElement encodingElement)
            ? encodingElement.GetString() ?? DefaultEncoding
            : DefaultEncoding;
        if (!secrets.TryGetValue("secret", out string? secret))
            throw new SourceVerificationException("Source verification secret field 'secret' is required.");

        if (!headers.TryGetValue(headerName, out string? signatureHeader)
            || !signatureHeader.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        byte[] provided;
        try
        {
            string encoded = signatureHeader[prefix.Length..];
            provided = encoding.Equals("base64", StringComparison.OrdinalIgnoreCase)
                ? Convert.FromBase64String(encoded)
                : Convert.FromHexString(encoded);
        }
        catch (FormatException)
        {
            return false;
        }

        byte[] expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), rawBody.Span);
        return CryptographicOperations.FixedTimeEquals(provided, expected);
    }
}
