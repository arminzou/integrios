using System.Text.Json;
using Integrios.Application.Delivery;
using Integrios.Domain.Entities;
using Integrios.Domain.Enums;
using Integrios.Domain.ValueObjects;

namespace Integrios.Application.Authoring.Connections;

internal static class ConnectionUseValidator
{
    public static void ValidateSourceReadiness(Connection connection, Connector connector)
    {
        if (connector.Direction == ConnectorDirection.Destination)
            throw Invalid("source", connector);

        ValidateConfiguration(connection.Config, connector.Manifest.SourceConfigurationSchema, "source");
        RequireSelection(
            connection.SourceVerification,
            connector.Manifest.SourceVerification.Schemes,
            connector.Manifest.SourceVerification.AllowUnverified,
            "source verification");
    }

    public static void ValidateDestinationReadiness(
        Connection connection,
        Connector connector,
        IDestinationAuthenticatorRegistry registry)
    {
        if (connector.Direction == ConnectorDirection.Source)
            throw Invalid("destination", connector);

        ValidateConfiguration(connection.Config, connector.Manifest.DestinationConfigurationSchema, "destination");
        ValidateDestinationBaseUri(connection.Config);

        ConnectionSchemeSelection? selection = RequireSelection(
            connection.DestinationAuthentication,
            connector.Manifest.DestinationAuthentication.Schemes,
            connector.Manifest.DestinationAuthentication.AllowUnauthenticated,
            "destination authentication");
        if (selection is not null && !registry.TryGet(selection.Scheme, out _))
        {
            throw new ConnectionValidationException(
                $"Destination authentication scheme '{selection.Scheme}' is not implemented.");
        }
    }

    public static void ValidateSourceAuthoring(Connection connection, Connector connector)
    {
        EnsureActive(connection, connector);
        ValidateSourceReadiness(connection, connector);
    }

    public static void ValidateDestinationAuthoring(
        Connection connection,
        Connector connector,
        IDestinationAuthenticatorRegistry registry)
    {
        EnsureActive(connection, connector);
        ValidateDestinationReadiness(connection, connector, registry);
    }

    private static ConnectionSchemeSelection? RequireSelection(
        ConnectionSchemeSelection? selection,
        IReadOnlyList<ConnectorSchemeManifest> supportedSchemes,
        bool allowAbsent,
        string use)
    {
        if (selection is null)
        {
            if (!allowAbsent)
                throw new ConnectionValidationException(
                    $"The Connection requires a {use} selection before it can serve this use.");
            return null;
        }

        if (supportedSchemes.Count == 0)
            throw new ConnectionValidationException(
                $"This Connector does not support a {use} selection.");

        ConnectorSchemeManifest? declaration = supportedSchemes.SingleOrDefault(
            scheme => scheme.Scheme.Equals(selection.Scheme, StringComparison.OrdinalIgnoreCase));
        if (declaration is null)
            throw new ConnectionValidationException(
                $"{use} scheme '{selection.Scheme}' is not supported by this Connector.");

        ValidateRequiredFields(selection.Config, declaration.RequiredConfig, use, "config");
        ValidateRequiredFields(selection.SecretRefs, declaration.RequiredSecretRefs, use, "secret_refs");
        return selection;
    }

    private static void ValidateRequiredFields(
        JsonElement value,
        IReadOnlyList<string> required,
        string use,
        string section)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw new ConnectionValidationException($"{use} {section} must be a JSON object.");
        foreach (string field in required)
        {
            if (!value.TryGetProperty(field, out JsonElement property) || property.ValueKind == JsonValueKind.Null)
                throw new ConnectionValidationException($"{use} {section} field '{field}' is required.");
        }
    }

    private static void ValidateConfiguration(JsonElement config, JsonElement? schema, string use)
    {
        if (schema is not JsonElement declaredSchema)
            throw new ConnectionValidationException(
                $"The Connector does not declare a {use} Connection configuration schema.");

        try
        {
            ConnectionConfigurationSchemaEvaluator.Validate(config, declaredSchema, $"Connection {use} config");
        }
        catch (ConnectionConfigurationValidationException exception)
        {
            throw new ConnectionValidationException(exception.Message);
        }
    }

    private static ConnectionValidationException Invalid(string use, Connector connector) =>
        new($"The Connection must use a Connector whose direction permits {use} use; '{connector.Key}' does not.");

    private static void EnsureActive(Connection connection, Connector connector)
    {
        if (connection.Status != OperationalStatus.Active)
            throw new ConnectionValidationException("The Connection must be active before this relationship can be established.");
        if (connector.Status != OperationalStatus.Active)
            throw new ConnectionValidationException("The Connection's Connector must be active before this relationship can be established.");
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
            throw new ConnectionValidationException(
                "Connection destination config must contain an absolute HTTP or HTTPS 'base_uri' with no query string or fragment.");
        }
    }
}
