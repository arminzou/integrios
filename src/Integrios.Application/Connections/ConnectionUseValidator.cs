using System.Text.Json;
using Integrios.Application.Auth;
using Integrios.Domain.Common;
using Integrios.Domain.Integrations;

namespace Integrios.Application.Connections;

internal static class ConnectionUseValidator
{
    public static void ValidateSourceReadiness(Connection connection, Integration integration)
    {
        if (integration.Direction == IntegrationDirection.Destination)
            throw Invalid("source", integration);

        ValidateConfiguration(connection.Config, integration.Manifest.SourceConfigurationSchema, "source");
        RequireSelection(
            connection.SourceVerification,
            integration.Manifest.SourceVerification.Schemes,
            integration.Manifest.SourceVerification.AllowUnverified,
            "source verification");
    }

    public static void ValidateDestinationReadiness(
        Connection connection,
        Integration integration,
        IAuthSchemeRegistry registry)
    {
        if (integration.Direction == IntegrationDirection.Source)
            throw Invalid("destination", integration);

        ValidateConfiguration(connection.Config, integration.Manifest.DestinationConfigurationSchema, "destination");
        ValidateDestinationBaseUri(connection.Config);

        ConnectionSchemeSelection? selection = RequireSelection(
            connection.DestinationAuthentication,
            integration.Manifest.DestinationAuthentication.Schemes,
            integration.Manifest.DestinationAuthentication.AllowUnauthenticated,
            "destination authentication");
        if (selection is not null && !registry.TryGet(selection.Scheme, out _))
        {
            throw new ConnectionValidationException(
                $"Destination authentication scheme '{selection.Scheme}' is not implemented.");
        }
    }

    public static void ValidateSourceAuthoring(Connection connection, Integration integration)
    {
        EnsureActive(connection, integration);
        ValidateSourceReadiness(connection, integration);
    }

    public static void ValidateDestinationAuthoring(
        Connection connection,
        Integration integration,
        IAuthSchemeRegistry registry)
    {
        EnsureActive(connection, integration);
        ValidateDestinationReadiness(connection, integration, registry);
    }

    private static ConnectionSchemeSelection? RequireSelection(
        ConnectionSchemeSelection? selection,
        IReadOnlyList<IntegrationSchemeManifest> supportedSchemes,
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
                $"This Integration does not support a {use} selection.");

        IntegrationSchemeManifest? declaration = supportedSchemes.SingleOrDefault(
            scheme => scheme.Scheme.Equals(selection.Scheme, StringComparison.OrdinalIgnoreCase));
        if (declaration is null)
            throw new ConnectionValidationException(
                $"{use} scheme '{selection.Scheme}' is not supported by this Integration.");

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
                $"The Integration does not declare a {use} Connection configuration schema.");

        try
        {
            ConnectionConfigurationSchemaEvaluator.Validate(config, declaredSchema, $"Connection {use} config");
        }
        catch (ConnectionConfigurationValidationException exception)
        {
            throw new ConnectionValidationException(exception.Message);
        }
    }

    private static ConnectionValidationException Invalid(string use, Integration integration) =>
        new($"The Connection must use an Integration whose direction permits {use} use; '{integration.Key}' does not.");

    private static void EnsureActive(Connection connection, Integration integration)
    {
        if (connection.Status != OperationalStatus.Active)
            throw new ConnectionValidationException("The Connection must be active before this relationship can be established.");
        if (integration.Status != OperationalStatus.Active)
            throw new ConnectionValidationException("The Connection's Integration must be active before this relationship can be established.");
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
