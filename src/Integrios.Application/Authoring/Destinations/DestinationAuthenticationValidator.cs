using System.Text.Json;
using Integrios.Application.Common;
using Integrios.Application.Delivery;
using Integrios.Application.Secrets;
using Integrios.Domain.Entities;
using Integrios.Domain.Enums;
using Integrios.Domain.ValueObjects;

namespace Integrios.Application.Authoring.Destinations;

internal static class DestinationAuthenticationValidator
{
    private static readonly JsonElement EmptyObject = JsonSerializer.Deserialize<JsonElement>("{}");

    public static DestinationAuthentication? Validate(
        Connector connector,
        DestinationAuthenticationInput? selection,
        IDestinationAuthenticatorRegistry registry)
    {
        IDestinationAuthenticator? handler = null;
        if (selection is not null && !registry.TryGet(selection.Scheme, out handler))
            throw new DestinationValidationException(
                $"Destination authentication scheme '{selection.Scheme}' is not implemented.");

        (string Scheme, JsonElement Config, JsonElement SecretRefs)? fields = Validate(
            connector,
            selection is null ? null : (selection.Scheme, selection.Config, selection.SecretRefs),
            connector.Manifest.DestinationAuthentication.Schemes,
            "destination authentication",
            handler);

        return fields is null
            ? null
            : new DestinationAuthentication
            {
                Scheme = fields.Value.Scheme,
                Config = fields.Value.Config,
                SecretRefs = fields.Value.SecretRefs
            };
    }

    private static (string Scheme, JsonElement Config, JsonElement SecretRefs)? Validate(
        Connector connector,
        (string Scheme, JsonElement Config, JsonElement SecretRefs)? selection,
        IReadOnlyList<ConnectorSchemeManifest> supportedSchemes,
        string capability,
        IDestinationAuthenticator? handler)
    {
        if (selection is null)
            return null;

        ConnectorSchemeManifest? declared = supportedSchemes.SingleOrDefault(
            scheme => scheme.Scheme.Equals(selection.Value.Scheme, StringComparison.OrdinalIgnoreCase));
        if (declared is null)
        {
            throw new DestinationValidationException(
                $"{capability} scheme '{selection.Value.Scheme}' is not supported by connector '{connector.Key}'.");
        }

        JsonElement config = NormalizeObject(selection.Value.Config);
        JsonElement secretRefs = NormalizeObject(selection.Value.SecretRefs);

        EnsureRequiredFields(config, declared.RequiredConfig, capability, "config");
        EnsureRequiredFields(secretRefs, declared.RequiredSecretRefs, capability, "secret_refs");
        if (handler is not null)
            EnsureOwnedHeadersAreSafe(handler, config);
        EnsureSecretReferencesAreSafe(secretRefs);
        if (selection.Value.Scheme.Equals("oauth2_client_credentials", StringComparison.OrdinalIgnoreCase))
            EnsureOAuthClientCredentialsAreSafe(config, secretRefs);

        return (declared.Scheme, config, secretRefs);
    }

    private static void EnsureOwnedHeadersAreSafe(IDestinationAuthenticator handler, JsonElement config)
    {
        IReadOnlyList<string> ownedHeaders;
        try
        {
            ownedHeaders = handler.GetOwnedHeaderNames(config);
        }
        catch (Exception)
        {
            throw new DestinationValidationException(
                "Destination authentication header configuration is invalid.");
        }

        foreach (string headerName in ownedHeaders)
        {
            if (!HttpHeaderName.IsValid(headerName))
                throw new DestinationValidationException(
                    $"Destination authentication header name '{headerName}' is invalid.");

            if (OutboundHttpHeaderRules.IsTransportOrPlatformOwned(headerName))
                throw new DestinationValidationException(
                    $"Header '{headerName}' is reserved for HTTP transport or Integrios delivery metadata.");
        }
    }

    private static JsonElement NormalizeObject(JsonElement value)
    {
        return value.ValueKind == JsonValueKind.Undefined ? EmptyObject : value;
    }

    private static void EnsureRequiredFields(
        JsonElement value,
        IReadOnlyList<string> requiredFields,
        string capability,
        string sectionName)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new DestinationValidationException($"{capability} {sectionName} must be a JSON object.");
        }

        foreach (string field in requiredFields)
        {
            if (!value.TryGetProperty(field, out JsonElement property) || property.ValueKind == JsonValueKind.Null)
            {
                throw new DestinationValidationException($"{capability} {sectionName} field '{field}' is required.");
            }
        }
    }

    private static void EnsureSecretReferencesAreSafe(JsonElement secretRefs)
    {
        if (SecretReferenceMap.Validate(secretRefs, "destination authentication secret_refs") is { } error)
            throw new DestinationValidationException(error);
    }

    private static void EnsureOAuthClientCredentialsAreSafe(JsonElement config, JsonElement secretRefs)
    {
        foreach (JsonProperty property in config.EnumerateObject())
        {
            if (property.Name is not ("token_endpoint" or "client_id" or "client_auth_method" or "scope"))
                throw new DestinationValidationException($"OAuth configuration field '{property.Name}' is not supported.");
        }

        foreach (JsonProperty property in secretRefs.EnumerateObject())
        {
            if (property.Name != "client_secret")
                throw new DestinationValidationException(
                    $"OAuth secret reference field '{property.Name}' is not supported.");
        }

        string tokenEndpoint = RequiredString(config, "token_endpoint");
        if (!Uri.TryCreate(tokenEndpoint, UriKind.Absolute, out Uri? endpoint)
            || endpoint.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(endpoint.UserInfo)
            || !string.IsNullOrEmpty(endpoint.Fragment))
        {
            throw new DestinationValidationException(
                "OAuth token endpoint must be an absolute HTTPS URL without user information or a fragment.");
        }

        _ = RequiredString(config, "client_id");
        string method = RequiredString(config, "client_auth_method");
        if (method is not ("client_secret_basic" or "client_secret_post"))
            throw new DestinationValidationException("OAuth client authentication method is invalid.");

        if (!config.TryGetProperty("scope", out JsonElement scope))
            return;
        if (scope.ValueKind != JsonValueKind.String
            || scope.GetString() is not { Length: > 0 and <= 1024 } value
            || value[0] == ' '
            || value[^1] == ' '
            || value.Contains("  ", StringComparison.Ordinal)
            || value.Any(character =>
                character != ' '
                && character is < (char)0x21 or (char)0x22 or (char)0x5C or > (char)0x7E))
        {
            throw new DestinationValidationException(
                "OAuth scope must be a space-delimited string of OAuth scope tokens up to 1024 characters.");
        }
    }

    private static string RequiredString(JsonElement value, string field)
    {
        if (!value.TryGetProperty(field, out JsonElement property)
            || property.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(property.GetString()))
        {
            throw new DestinationValidationException(
                $"Destination authentication config field '{field}' must be a non-empty string.");
        }

        return property.GetString()!;
    }
}
