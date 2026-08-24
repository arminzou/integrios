using System.Text;
using Integrios.Application.Delivery;
using Integrios.Domain.Entities;
using Integrios.Domain.ValueObjects;

namespace Integrios.Application.Authoring.Subscriptions;

internal static class HttpDeliveryConfigurationRules
{
    private const int MaxHeaders = 32;
    private const int MaxHeaderValueBytes = 8 * 1024;

    private static readonly HashSet<string> AllowedMethods = new(StringComparer.Ordinal)
    {
        "POST", "PUT", "PATCH", "DELETE"
    };

    public static void Validate(HttpDeliveryConfiguration config)
    {
        if (config.Version != HttpDeliveryConfiguration.CurrentVersion)
            throw Invalid($"http_delivery.version must be {HttpDeliveryConfiguration.CurrentVersion}.");

        if (!AllowedMethods.Contains(config.Method))
            throw Invalid("http_delivery.method must be POST, PUT, PATCH, or DELETE.");

        string? pathError = HttpTargetComposer.ValidateRelativeTarget(config.Path);
        if (pathError is not null)
            throw Invalid(pathError);

        if (config.Body is not ("json" or "none"))
            throw Invalid("http_delivery.body must be 'json' or 'none'.");

        if (config.Headers is null)
            throw Invalid("http_delivery.headers must be an object.");

        if (config.Headers.Count > MaxHeaders)
            throw Invalid($"http_delivery.headers must contain at most {MaxHeaders} entries.");

        if (config.Headers.Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() != config.Headers.Count)
            throw Invalid("http_delivery.headers must not contain names that differ only by case.");

        foreach ((string name, string value) in config.Headers)
        {
            if (!OutboundHttpHeaderRules.IsValidName(name))
                throw Invalid($"http_delivery header name '{name}' is invalid.");
            if (OutboundHttpHeaderRules.IsReservedForStaticConfiguration(name))
                throw Invalid($"http_delivery header '{name}' is reserved and cannot be configured.");
            if (value is null)
                throw Invalid($"http_delivery header '{name}' value must be a string.");
            if (value.Contains('\r') || value.Contains('\n') || value.Contains('\0'))
                throw Invalid($"http_delivery header '{name}' contains prohibited characters.");
            if (Encoding.UTF8.GetByteCount(value) > MaxHeaderValueBytes)
                throw Invalid($"http_delivery header '{name}' must not exceed 8 KiB of UTF-8 text.");
        }
    }

    public static void ValidateAuthenticationHeaderCollisions(
        HttpDeliveryConfiguration config,
        ConnectionSchemeSelection? destinationAuthentication,
        IDestinationAuthenticatorRegistry authSchemeRegistry)
    {
        if (destinationAuthentication is null)
            return;

        IDestinationAuthenticator handler = authSchemeRegistry.GetRequired(destinationAuthentication.Scheme);
        foreach (string headerName in handler.GetOwnedHeaderNames(destinationAuthentication.Config))
        {
            if (config.Headers.Keys.Any(name => string.Equals(name, headerName, StringComparison.OrdinalIgnoreCase)))
                throw Invalid($"http_delivery header '{headerName}' is owned by destination authentication.");
        }
    }

    private static SubscriptionValidationException Invalid(string message) => new(message);
}
