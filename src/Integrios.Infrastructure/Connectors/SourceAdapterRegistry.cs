using System.Text.Json;
using Integrios.Application.Connectors;

namespace Integrios.Infrastructure.Connectors;

internal sealed class SourceAdapterRegistry : ISourceAdapterRegistry
{
    private const int MaxHeaderNameLength = 200;
    private const int MaxPrefixLength = 32;

    private static readonly HashSet<string> VerifiedWebhookConfigProperties = new(
        [
            "signature_header",
            "signature_encoding",
            "signature_prefix",
            "delivery_id_header",
            "event_type_header",
            "event_type_action_field",
        ],
        StringComparer.Ordinal);

    private static readonly SourceAdapterRegistration VerifiedWebhookV1 = new(
        Key: "verified_webhook",
        ContractVersion: 1,
        AuthoringSafe: true,
        AllowsUnverifiedUse: false,
        CompatibleSourceVerificationSchemes: ["hmac_sha256"],
        ValidateConfig: ValidateVerifiedWebhookConfig);

    private static readonly IReadOnlyDictionary<(string Key, int ContractVersion), SourceAdapterRegistration> Registrations =
        new[] { VerifiedWebhookV1 }.ToDictionary(registration => (registration.Key, registration.ContractVersion));

    public bool TryGet(string key, int contractVersion, out SourceAdapterRegistration registration) =>
        Registrations.TryGetValue((key, contractVersion), out registration!);

    public IReadOnlyCollection<SourceAdapterRegistration> GetAll() => Registrations.Values.ToArray();

    private static void ValidateVerifiedWebhookConfig(JsonElement config)
    {
        if (config.ValueKind != JsonValueKind.Object)
            throw Invalid("source_contracts[].config must be an object.");

        foreach (JsonProperty property in config.EnumerateObject())
        {
            if (!VerifiedWebhookConfigProperties.Contains(property.Name))
                throw Invalid($"source_contracts[].config contains unsupported property '{property.Name}'.");
        }

        RequireHeaderName(config, "signature_header");
        RequireEncoding(config);
        AllowOptionalBoundedString(config, "signature_prefix", MaxPrefixLength);
        RequireHeaderName(config, "delivery_id_header");
        RequireHeaderName(config, "event_type_header");
        AllowOptionalBoundedString(config, "event_type_action_field", MaxHeaderNameLength);
    }

    private static void RequireHeaderName(JsonElement config, string property)
    {
        string? headerName = config.TryGetProperty(property, out JsonElement value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        if (string.IsNullOrEmpty(headerName)
            || headerName.Length > MaxHeaderNameLength
            || !headerName.All(IsHttpTokenCharacter))
        {
            throw Invalid($"source_contracts[].config.{property} is required and must be a valid HTTP header name.");
        }
    }

    private static bool IsHttpTokenCharacter(char value) =>
        value is >= '0' and <= '9'
            or >= 'A' and <= 'Z'
            or >= 'a' and <= 'z'
        || "!#$%&'*+-.^_`|~".Contains(value, StringComparison.Ordinal);

    private static void RequireEncoding(JsonElement config)
    {
        if (!config.TryGetProperty("signature_encoding", out JsonElement value)
            || value.ValueKind != JsonValueKind.String
            || value.GetString() is not ("hex" or "base64"))
        {
            throw Invalid("source_contracts[].config.signature_encoding is required and must be 'hex' or 'base64'.");
        }
    }

    private static void AllowOptionalBoundedString(JsonElement config, string property, int maxLength)
    {
        if (!config.TryGetProperty(property, out JsonElement value))
            return;

        if (value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString())
            || value.GetString()!.Length > maxLength)
        {
            throw Invalid($"source_contracts[].config.{property} must be a non-empty bounded string when present.");
        }
    }

    private static ConnectorManifestValidationException Invalid(string message) => new(message);
}
