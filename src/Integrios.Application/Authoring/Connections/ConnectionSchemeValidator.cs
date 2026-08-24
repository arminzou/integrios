using System.Text.Json;
using Integrios.Application.Delivery;
using Integrios.Application.Secrets;
using Integrios.Domain.Entities;
using Integrios.Domain.Enums;
using Integrios.Domain.ValueObjects;

namespace Integrios.Application.Authoring.Connections;

internal static partial class ConnectionSchemeValidator
{
    private static readonly JsonElement EmptyObject = JsonSerializer.Deserialize<JsonElement>("{}");

    public static SourceVerification? ValidateSource(
        Connector connector,
        SourceVerificationInput? selection)
    {
        EnsureDirection(connector, source: true, selection is not null);
        (string Scheme, JsonElement Config, JsonElement SecretRefs)? fields = Validate(
            connector,
            selection is null ? null : (selection.Scheme, selection.Config, selection.SecretRefs),
            connector.Manifest.SourceVerification.Schemes,
            "source verification",
            handler: null);

        return fields is null
            ? null
            : new SourceVerification
            {
                Scheme = fields.Value.Scheme,
                Config = fields.Value.Config,
                SecretRefs = fields.Value.SecretRefs
            };
    }

    public static DestinationAuthentication? ValidateDestination(
        Connector connector,
        DestinationAuthenticationInput? selection,
        IDestinationAuthenticatorRegistry registry)
    {
        EnsureDirection(connector, source: false, selection is not null);
        IDestinationAuthenticator? handler = null;
        if (selection is not null && !registry.TryGet(selection.Scheme, out handler))
            throw new ConnectionValidationException(
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
            throw new ConnectionValidationException(
                $"{capability} scheme '{selection.Value.Scheme}' is not supported by connector '{connector.Key}'.");
        }

        JsonElement config = NormalizeObject(selection.Value.Config);
        JsonElement secretRefs = NormalizeObject(selection.Value.SecretRefs);

        EnsureRequiredFields(config, declared.RequiredConfig, capability, "config");
        EnsureRequiredFields(secretRefs, declared.RequiredSecretRefs, capability, "secret_refs");
        if (handler is not null)
            EnsureOwnedHeadersAreSafe(handler, config);
        EnsureSecretReferencesAreSafe(secretRefs);

        return (declared.Scheme, config, secretRefs);
    }

    private static void EnsureDirection(
        Connector connector,
        bool source,
        bool hasSelection)
    {
        if (!hasSelection)
            return;

        bool capable = source
            ? connector.Direction is ConnectorDirection.Source or ConnectorDirection.Both
            : connector.Direction is ConnectorDirection.Destination or ConnectorDirection.Both;
        if (!capable)
        {
            throw new ConnectionValidationException(
                $"Connector '{connector.Key}' does not permit {(source ? "source verification" : "destination authentication")}.");
        }
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
            throw new ConnectionValidationException(
                "Destination authentication header configuration is invalid.");
        }

        foreach (string headerName in ownedHeaders)
        {
            if (!OutboundHttpHeaderRules.IsValidName(headerName))
                throw new ConnectionValidationException(
                    $"Destination authentication header name '{headerName}' is invalid.");

            if (OutboundHttpHeaderRules.IsTransportOrPlatformOwned(headerName))
                throw new ConnectionValidationException(
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
            throw new ConnectionValidationException($"{capability} {sectionName} must be a JSON object.");
        }

        foreach (string field in requiredFields)
        {
            if (!value.TryGetProperty(field, out JsonElement property) || property.ValueKind == JsonValueKind.Null)
            {
                throw new ConnectionValidationException($"{capability} {sectionName} field '{field}' is required.");
            }
        }
    }

    private static void EnsureSecretReferencesAreSafe(JsonElement secretRefs)
    {
        foreach (JsonProperty property in secretRefs.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String)
            {
                throw new ConnectionValidationException(
                    $"Secret reference '{property.Name}' must be a lowercase snake_case string.");
            }

            string value = property.Value.GetString() ?? "";
            if (!SecretReferenceName.IsValid(value))
            {
                throw new ConnectionValidationException(
                    $"Secret reference '{property.Name}' must be a lowercase logical name of 1 to 63 characters.");
            }
        }
    }
}
