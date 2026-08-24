using System.Text;
using Integrios.Application.Secrets;
using Integrios.Application.Delivery;
using Integrios.Domain.Entities;
using Integrios.Domain.ValueObjects;

namespace Integrios.Infrastructure.Secrets;

internal static class SecretValueValidator
{
    public const int MaxBytes = 64 * 1024;
    public static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static void ValidateScope(TenantSecretScope tenant, string secretReference, string providerName)
    {
        if (!TenantSlug.IsValid(tenant.Slug) || !SecretReferenceName.IsValid(secretReference))
            throw new SecretResolutionException(secretReference, providerName);
    }

    public static void EnsureHeaderSafe(string value, string secretField)
    {
        if (value.Contains('\r') || value.Contains('\n'))
        {
            throw new DeliveryConfigurationException(
                $"Auth secret field '{secretField}' contains a line break, which is not permitted in an HTTP header value.");
        }
    }

    // Edge CR/LF is never a legitimate byte of a secret value - no HTTP header can carry raw CRLF,
    // and no scheme this platform supports defines a credential that includes one - so it is always
    // a storage or editor artifact (a trailing newline from a text editor's "insert final newline
    // on save", for example). Trimming only the edges, never the interior, means a genuinely
    // corrupted value with an embedded line break still fails loud downstream instead of being
    // silently accepted.
    public static string ValidateText(string? value, string secretReference, string providerName)
    {
        try
        {
            string? trimmed = value?.Trim('\r', '\n');
            if (string.IsNullOrEmpty(trimmed)
                || trimmed.Contains('\0', StringComparison.Ordinal)
                || StrictUtf8.GetByteCount(trimmed) > MaxBytes)
            {
                throw new SecretResolutionException(secretReference, providerName);
            }

            return trimmed;
        }
        catch (EncoderFallbackException)
        {
            throw new SecretResolutionException(secretReference, providerName);
        }
    }
}
