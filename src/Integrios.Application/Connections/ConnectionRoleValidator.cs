using System.Text.Json;
using Integrios.Application.Auth;
using Integrios.Domain.Integrations;

namespace Integrios.Application.Connections;

internal static class ConnectionRoleValidator
{
    public static void ValidateSource(Connection connection, Integration integration)
    {
        EnsureActive(connection, integration);
        if (integration.Direction == IntegrationDirection.Destination)
            throw Invalid("source", integration);

        ValidateConfiguration(connection.Config, integration.Manifest.SourceConfigurationSchema, "source");
        RequireSelection(
            connection.SourceVerification,
            integration.Manifest.SourceVerificationSchemes,
            "source verification");
    }

    public static void ValidateDestination(
        Connection connection,
        Integration integration,
        IAuthSchemeRegistry registry)
    {
        EnsureActive(connection, integration);
        if (integration.Direction == IntegrationDirection.Source)
            throw Invalid("destination", integration);

        ValidateConfiguration(connection.Config, integration.Manifest.DestinationConfigurationSchema, "destination");
        if (integration.Key == "webhook")
            ValidateLegacyWebhookUrl(connection.Config);

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

    private static ConnectionSchemeSelection? RequireSelection(
        ConnectionSchemeSelection? selection,
        IReadOnlyList<IntegrationSchemeManifest> supportedSchemes,
        string capability)
    {
        if (supportedSchemes.Count == 0)
        {
            if (selection is not null)
                throw new ConnectionRequestValidationException(
                    $"This Integration does not support a {capability} selection.");
            return null;
        }

        if (selection is null)
            throw new ConnectionRequestValidationException(
                $"The Connection requires a {capability} selection before it can be used in this role.");

        IntegrationSchemeManifest? declaration = supportedSchemes.SingleOrDefault(
            scheme => scheme.Scheme.Equals(selection.Scheme, StringComparison.OrdinalIgnoreCase));
        if (declaration is null)
            throw new ConnectionRequestValidationException(
                $"{capability} scheme '{selection.Scheme}' is not supported by this Integration.");

        ValidateRequiredFields(selection.Config, declaration.RequiredConfig, capability, "config");
        ValidateRequiredFields(selection.SecretRefs, declaration.RequiredSecretRefs, capability, "secret_refs");
        return selection;
    }

    private static void ValidateRequiredFields(
        JsonElement value,
        IReadOnlyList<string> required,
        string capability,
        string section)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw new ConnectionRequestValidationException($"{capability} {section} must be a JSON object.");
        foreach (string field in required)
        {
            if (!value.TryGetProperty(field, out JsonElement property) || property.ValueKind == JsonValueKind.Null)
                throw new ConnectionRequestValidationException($"{capability} {section} field '{field}' is required.");
        }
    }

    private static void ValidateConfiguration(JsonElement config, JsonElement? schema, string role)
    {
        if (schema is not JsonElement declaredSchema)
            throw new ConnectionRequestValidationException(
                $"The Integration does not declare a {role} Connection configuration schema.");

        try
        {
            ConnectionConfigurationSchemaEvaluator.Validate(config, declaredSchema, $"Connection {role} config");
        }
        catch (ConnectionConfigurationValidationException exception)
        {
            throw new ConnectionRequestValidationException(exception.Message);
        }
    }

    private static ConnectionRequestValidationException Invalid(string role, Integration integration) =>
        new($"The Connection must use an Integration whose direction permits {role} use; '{integration.Key}' does not.");

    private static void EnsureActive(Connection connection, Integration integration)
    {
        if (connection.Status != Domain.Common.OperationalStatus.Active)
            throw new ConnectionRequestValidationException("The Connection must be active before it can be used.");
        if (integration.Status != Domain.Common.OperationalStatus.Active)
            throw new ConnectionRequestValidationException("The Connection's Integration must be active before it can be used.");
    }

    private static void ValidateLegacyWebhookUrl(JsonElement config)
    {

        if (config.ValueKind != JsonValueKind.Object
            || !config.TryGetProperty("url", out JsonElement urlElement)
            || urlElement.ValueKind != JsonValueKind.String
            || !Uri.TryCreate(urlElement.GetString(), UriKind.Absolute, out Uri? uri)
            || uri.Scheme is not ("http" or "https"))
        {
            throw new ConnectionRequestValidationException(
                "Connection config must contain an absolute HTTP or HTTPS 'url'.");
        }
    }
}
