using System.Text;
using Integrios.Application.Secrets;
using Integrios.Application.Delivery;
using Integrios.Domain.Tenants;

namespace Integrios.Infrastructure.Http.Auth;

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

    public static string ValidateText(string? value, string secretReference, string providerName)
    {
        try
        {
            if (string.IsNullOrEmpty(value)
                || value.Contains('\0', StringComparison.Ordinal)
                || StrictUtf8.GetByteCount(value) > MaxBytes)
            {
                throw new SecretResolutionException(secretReference, providerName);
            }

            return value;
        }
        catch (EncoderFallbackException)
        {
            throw new SecretResolutionException(secretReference, providerName);
        }
    }
}
