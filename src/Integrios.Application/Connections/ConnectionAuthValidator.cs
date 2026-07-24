using System.Text.Json;
using Integrios.Application.Abstractions.Auth;
using Integrios.Domain.Integrations;

namespace Integrios.Application.Connections;

internal static partial class ConnectionAuthValidator
{
    private static readonly JsonElement EmptyObject = JsonSerializer.Deserialize<JsonElement>("{}");
    private static readonly string[] ReservedDeliveryHeaders =
    [
        "Integrios-Event-Id",
        "Integrios-Delivery-Id",
        "Integrios-Attempt-Id",
        "Integrios-Attempt-Number"
    ];

    public static ConnectionAuth? Validate(Integration integration, ConnectionAuthInput? auth, IAuthSchemeRegistry registry)
    {
        if (auth is null)
        {
            if (integration.SupportedAuthSchemes.Count == 0)
            {
                return null;
            }

            throw new ConnectionRequestValidationException(
                "This integration requires an auth selection; no-auth connections are only valid for open integrations.");
        }

        if (!integration.SupportedAuthSchemes.Contains(auth.Scheme, StringComparer.OrdinalIgnoreCase))
        {
            throw new ConnectionRequestValidationException(
                $"Auth scheme '{auth.Scheme}' is not supported by integration '{integration.Key}'.");
        }

        if (!registry.TryGet(auth.Scheme, out IAuthSchemeHandler? handler))
        {
            throw new ConnectionRequestValidationException($"Auth scheme '{auth.Scheme}' is not implemented.");
        }

        JsonElement config = NormalizeObject(auth.Config);
        JsonElement secretRefs = NormalizeObject(auth.SecretRefs);

        EnsureRequiredFields(config, handler.RequiredConfigFields, "config");
        EnsureRequiredFields(secretRefs, handler.RequiredSecretFields, "secret_refs");
        EnsureReservedHeadersAreNotConfigured(handler.Name, config);
        EnsureSecretReferencesAreSafe(secretRefs);

        return new ConnectionAuth
        {
            Scheme = handler.Name,
            Config = config,
            SecretRefs = secretRefs
        };
    }

    private static void EnsureReservedHeadersAreNotConfigured(string scheme, JsonElement config)
    {
        if (!scheme.Equals("api_key_header", StringComparison.OrdinalIgnoreCase)
            || !config.TryGetProperty("header_name", out JsonElement headerElement)
            || headerElement.ValueKind != JsonValueKind.String)
        {
            return;
        }

        string headerName = headerElement.GetString() ?? string.Empty;
        if (ReservedDeliveryHeaders.Contains(headerName, StringComparer.OrdinalIgnoreCase))
        {
            throw new ConnectionRequestValidationException(
                $"Header '{headerName}' is reserved for Integrios delivery identity metadata.");
        }
    }

    private static JsonElement NormalizeObject(JsonElement value)
    {
        return value.ValueKind == JsonValueKind.Undefined ? EmptyObject : value;
    }

    private static void EnsureRequiredFields(JsonElement value, IReadOnlyList<string> requiredFields, string sectionName)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new ConnectionRequestValidationException($"Auth {sectionName} must be a JSON object.");
        }

        foreach (string field in requiredFields)
        {
            if (!value.TryGetProperty(field, out JsonElement property) || property.ValueKind == JsonValueKind.Null)
            {
                throw new ConnectionRequestValidationException($"Auth {sectionName} field '{field}' is required.");
            }
        }
    }

    private static void EnsureSecretReferencesAreSafe(JsonElement secretRefs)
    {
        foreach (JsonProperty property in secretRefs.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String)
            {
                throw new ConnectionRequestValidationException(
                    $"Secret reference '{property.Name}' must be a lowercase snake_case string.");
            }

            string value = property.Value.GetString() ?? "";
            if (!SecretReferenceName.IsValid(value))
            {
                throw new ConnectionRequestValidationException(
                    $"Secret reference '{property.Name}' must be a lowercase logical name of 1 to 63 characters.");
            }
        }
    }
}
