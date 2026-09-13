using System.Text.Json;
using Integrios.Application.Common;
using Integrios.Application.Delivery;
using Integrios.Domain.Entities;
using Integrios.Domain.Enums;
using Integrios.Domain.ValueObjects;

namespace Integrios.Application.Authoring.Destinations;

internal static class DestinationUseValidator
{
    public static void ValidateAuthoring(
        Destination destination,
        Connector connector,
        IDestinationAuthenticatorRegistry registry)
    {
        if (connector.Direction == ConnectorDirection.Source)
            throw new DestinationValidationException(
                $"The Connector '{connector.Key}' does not permit Destination authoring.");

        EnsureActive(destination, connector);
        ValidateConfiguration(destination.Configuration, connector.Manifest.DestinationConfigurationSchema);
        ValidateDestinationBaseUri(destination.Configuration);

        (string Scheme, JsonElement Config, JsonElement SecretRefs)? selection = RequireSelection(
            destination.Authentication is null
                ? null
                : (destination.Authentication.Scheme, destination.Authentication.Config, destination.Authentication.SecretRefs),
            connector.Manifest.DestinationAuthentication.Schemes,
            connector.Manifest.DestinationAuthentication.AllowUnauthenticated,
            "destination authentication");
        if (selection is not null && !registry.TryGet(selection.Value.Scheme, out _))
        {
            throw new DestinationValidationException(
                $"Destination authentication scheme '{selection.Value.Scheme}' is not implemented.");
        }
    }

    private static (string Scheme, JsonElement Config, JsonElement SecretRefs)? RequireSelection(
        (string Scheme, JsonElement Config, JsonElement SecretRefs)? selection,
        IReadOnlyList<ConnectorSchemeManifest> supportedSchemes,
        bool allowAbsent,
        string use)
    {
        if (selection is null)
        {
            if (!allowAbsent)
                throw new DestinationValidationException(
                    $"The Destination requires a {use} selection before it can be active.");
            return null;
        }

        if (supportedSchemes.Count == 0)
            throw new DestinationValidationException(
                $"This Connector does not support a {use} selection.");

        ConnectorSchemeManifest? declaration = supportedSchemes.SingleOrDefault(
            scheme => scheme.Scheme.Equals(selection.Value.Scheme, StringComparison.OrdinalIgnoreCase));
        if (declaration is null)
            throw new DestinationValidationException(
                $"{use} scheme '{selection.Value.Scheme}' is not supported by this Connector.");

        ValidateRequiredFields(selection.Value.Config, declaration.RequiredConfig, use, "config");
        ValidateRequiredFields(selection.Value.SecretRefs, declaration.RequiredSecretRefs, use, "secret_refs");
        return selection;
    }

    private static void ValidateRequiredFields(
        JsonElement value,
        IReadOnlyList<string> required,
        string use,
        string section)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw new DestinationValidationException($"{use} {section} must be a JSON object.");
        foreach (string field in required)
        {
            if (!value.TryGetProperty(field, out JsonElement property) || property.ValueKind == JsonValueKind.Null)
                throw new DestinationValidationException($"{use} {section} field '{field}' is required.");
        }
    }

    private static void ValidateConfiguration(JsonElement configuration, JsonElement? schema)
    {
        if (schema is not JsonElement declaredSchema)
            throw new DestinationValidationException(
                "The Connector does not declare a Destination configuration schema.");

        try
        {
            ConfigurationSchemaEvaluator.Validate(configuration, declaredSchema, "Destination configuration");
        }
        catch (ConfigurationValidationException exception)
        {
            throw new DestinationValidationException(exception.Message);
        }
    }

    private static void EnsureActive(Destination destination, Connector connector)
    {
        if (destination.Status != OperationalStatus.Active)
            throw new DestinationValidationException("The Destination must be active before it can be used.");
        if (connector.Status != OperationalStatus.Active)
            throw new DestinationValidationException("The Destination's Connector must be active before it can be used.");
    }

    private static void ValidateDestinationBaseUri(JsonElement config)
    {
        if (config.ValueKind != JsonValueKind.Object
            || !config.TryGetProperty("base_uri", out JsonElement baseUriElement)
            || baseUriElement.ValueKind != JsonValueKind.String
            || !Uri.TryCreate(baseUriElement.GetString(), UriKind.Absolute, out Uri? uri)
            || uri.Scheme is not ("http" or "https")
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new DestinationValidationException(
                "Destination configuration must contain an absolute HTTP or HTTPS 'base_uri' with no query string or fragment.");
        }
    }
}
