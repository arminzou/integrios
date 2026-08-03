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
            integration.Manifest.SourceVerificationSchemes,
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
        ValidateHttpBaseUri(connection.Config);

        ConnectionSchemeSelection? selection = RequireSelection(
            connection.DestinationAuthentication,
            integration.Manifest.DestinationAuthenticationSchemes,
            "destination authentication");
        if (selection is not null && !registry.TryGet(selection.Scheme, out _))
        {
            throw new ConnectionRequestValidationException(
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
        string use)
    {
        if (supportedSchemes.Count == 0)
        {
            if (selection is not null)
                throw new ConnectionRequestValidationException(
                    $"This Integration does not support a {use} selection.");
            return null;
        }

        if (selection is null)
            throw new ConnectionRequestValidationException(
                $"The Connection requires a {use} selection before it can serve this use.");

        IntegrationSchemeManifest? declaration = supportedSchemes.SingleOrDefault(
            scheme => scheme.Scheme.Equals(selection.Scheme, StringComparison.OrdinalIgnoreCase));
        if (declaration is null)
            throw new ConnectionRequestValidationException(
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
            throw new ConnectionRequestValidationException($"{use} {section} must be a JSON object.");
        foreach (string field in required)
        {
            if (!value.TryGetProperty(field, out JsonElement property) || property.ValueKind == JsonValueKind.Null)
                throw new ConnectionRequestValidationException($"{use} {section} field '{field}' is required.");
        }
    }

    private static void ValidateConfiguration(JsonElement config, JsonElement? schema, string use)
    {
        if (schema is not JsonElement declaredSchema)
            throw new ConnectionRequestValidationException(
                $"The Integration does not declare a {use} Connection configuration schema.");

        try
        {
            ConnectionConfigurationSchemaEvaluator.Validate(config, declaredSchema, $"Connection {use} config");
        }
        catch (ConnectionConfigurationValidationException exception)
        {
            throw new ConnectionRequestValidationException(exception.Message);
        }
    }

    private static ConnectionRequestValidationException Invalid(string use, Integration integration) =>
        new($"The Connection must use an Integration whose direction permits {use} use; '{integration.Key}' does not.");

    private static void EnsureActive(Connection connection, Integration integration)
    {
        if (connection.Status != OperationalStatus.Active)
            throw new ConnectionRequestValidationException("The Connection must be active before this relationship can be established.");
        if (integration.Status != OperationalStatus.Active)
            throw new ConnectionRequestValidationException("The Connection's Integration must be active before this relationship can be established.");
    }

    private static void ValidateHttpBaseUri(JsonElement config)
    {
        if (config.ValueKind != JsonValueKind.Object
            || !config.TryGetProperty("url", out JsonElement urlElement)
            || urlElement.ValueKind != JsonValueKind.String
            || !Uri.TryCreate(urlElement.GetString(), UriKind.Absolute, out Uri? uri)
            || uri.Scheme is not ("http" or "https"))
        {
            throw new ConnectionRequestValidationException(
                "Connection destination config must contain an absolute HTTP or HTTPS 'url'.");
        }
    }
}
