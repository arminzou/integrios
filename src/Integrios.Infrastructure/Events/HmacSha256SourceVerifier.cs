using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Integrios.Application.Ingestion;

namespace Integrios.Infrastructure.Events;

// Generic HMAC-SHA256-over-raw-body verification: which header carries the signature, how it is
// encoded, and any literal prefix (e.g. GitHub's "sha256=") are declared per Connector, not
// hardcoded to one provider.
internal sealed class HmacSha256SourceVerifier : ISourceVerifier
{
    public string Scheme => "hmac_sha256";
    public IReadOnlyList<string> RequiredConfigFields => ["header_name"];
    public IReadOnlyList<string> RequiredSecretFields => ["secret"];

    public bool Verify(
        ReadOnlyMemory<byte> rawBody,
        IReadOnlyDictionary<string, string> headers,
        JsonElement config,
        IReadOnlyDictionary<string, string> secrets)
    {
        string headerName = config.GetProperty("header_name").GetString()
            ?? throw new SourceVerificationException("Source verification config field 'header_name' is required.");
        string prefix = config.TryGetProperty("prefix", out JsonElement prefixElement)
            ? prefixElement.GetString() ?? ""
            : "";
        string encoding = config.TryGetProperty("encoding", out JsonElement encodingElement)
            ? encodingElement.GetString() ?? "hex"
            : "hex";
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
