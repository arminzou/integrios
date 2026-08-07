using System.Text.Json;
using Integrios.Application.Auth;
using Integrios.Application.Delivery;
using Integrios.Application.Secrets;
using Integrios.Domain.Connections;
using Integrios.Domain.Integrations;

namespace Integrios.Application.Connections;

internal static partial class ConnectionSchemeSelectionValidator
{
    private static readonly JsonElement EmptyObject = JsonSerializer.Deserialize<JsonElement>("{}");

    public static ConnectionSchemeSelection? ValidateSource(
        Integration integration,
        ConnectionSchemeSelectionInput? selection)
    {
        EnsureDirection(integration, source: true, selection);
        return Validate(
            integration,
            selection,
            integration.Manifest.SourceVerification.Schemes,
            "source verification",
            handler: null);
    }

    public static ConnectionSchemeSelection? ValidateDestination(
        Integration integration,
        ConnectionSchemeSelectionInput? selection,
        IAuthSchemeRegistry registry)
    {
        EnsureDirection(integration, source: false, selection);
        IAuthSchemeHandler? handler = null;
        if (selection is not null && !registry.TryGet(selection.Scheme, out handler))
            throw new ConnectionValidationException(
                $"Destination authentication scheme '{selection.Scheme}' is not implemented.");

        return Validate(
            integration,
            selection,
            integration.Manifest.DestinationAuthentication.Schemes,
            "destination authentication",
            handler);
    }

    private static ConnectionSchemeSelection? Validate(
        Integration integration,
        ConnectionSchemeSelectionInput? selection,
        IReadOnlyList<IntegrationSchemeManifest> supportedSchemes,
        string capability,
        IAuthSchemeHandler? handler)
    {
        if (selection is null)
            return null;

        IntegrationSchemeManifest? declared = supportedSchemes.SingleOrDefault(
            scheme => scheme.Scheme.Equals(selection.Scheme, StringComparison.OrdinalIgnoreCase));
        if (declared is null)
        {
            throw new ConnectionValidationException(
                $"{capability} scheme '{selection.Scheme}' is not supported by integration '{integration.Key}'.");
        }

        JsonElement config = NormalizeObject(selection.Config);
        JsonElement secretRefs = NormalizeObject(selection.SecretRefs);

        EnsureRequiredFields(config, declared.RequiredConfig, capability, "config");
        EnsureRequiredFields(secretRefs, declared.RequiredSecretRefs, capability, "secret_refs");
        if (handler is not null)
            EnsureOwnedHeadersAreSafe(handler, config);
        EnsureSecretReferencesAreSafe(secretRefs);

        return new ConnectionSchemeSelection
        {
            Scheme = declared.Scheme,
            Config = config,
            SecretRefs = secretRefs
        };
    }

    private static void EnsureDirection(
        Integration integration,
        bool source,
        ConnectionSchemeSelectionInput? selection)
    {
        if (selection is null)
            return;

        bool capable = source
            ? integration.Direction is IntegrationDirection.Source or IntegrationDirection.Both
            : integration.Direction is IntegrationDirection.Destination or IntegrationDirection.Both;
        if (!capable)
        {
            throw new ConnectionValidationException(
                $"Integration '{integration.Key}' does not permit {(source ? "source verification" : "destination authentication")}.");
        }
    }

    private static void EnsureOwnedHeadersAreSafe(IAuthSchemeHandler handler, JsonElement config)
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
