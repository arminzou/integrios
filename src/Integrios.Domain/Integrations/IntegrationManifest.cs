using System.Text.Json;
using System.Text.Json.Serialization;

namespace Integrios.Domain.Integrations;

public sealed record IntegrationManifest
{
    [JsonPropertyName("manifest_schema_version")]
    public required int ManifestSchemaVersion { get; init; }

    [JsonPropertyName("key")]
    public required string Key { get; init; }

    [JsonPropertyName("contract_version")]
    public required int ContractVersion { get; init; }

    [JsonPropertyName("direction")]
    public required string Direction { get; init; }

    [JsonPropertyName("source_configuration_schema")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? SourceConfigurationSchema { get; init; }

    [JsonPropertyName("destination_configuration_schema")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? DestinationConfigurationSchema { get; init; }

    [JsonPropertyName("source_verification")]
    public required IntegrationSourceVerificationManifest SourceVerification { get; init; }

    [JsonPropertyName("destination_authentication")]
    public required IntegrationDestinationAuthenticationManifest DestinationAuthentication { get; init; }

    [JsonPropertyName("built_in_source_adapter")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BuiltInSourceAdapter { get; init; }

    [JsonPropertyName("http_outcome")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? HttpOutcome { get; init; }

    [JsonPropertyName("presentation")]
    public required IntegrationPresentationManifest Presentation { get; init; }
}

public sealed record IntegrationSourceVerificationManifest
{
    [JsonPropertyName("allow_unverified")]
    public required bool AllowUnverified { get; init; }

    [JsonPropertyName("schemes")]
    public IReadOnlyList<IntegrationSchemeManifest> Schemes { get; init; } = [];
}

public sealed record IntegrationDestinationAuthenticationManifest
{
    [JsonPropertyName("allow_unauthenticated")]
    public required bool AllowUnauthenticated { get; init; }

    [JsonPropertyName("schemes")]
    public IReadOnlyList<IntegrationSchemeManifest> Schemes { get; init; } = [];
}

public sealed record IntegrationSchemeManifest
{
    [JsonPropertyName("scheme")]
    public required string Scheme { get; init; }

    [JsonPropertyName("required_config")]
    public IReadOnlyList<string> RequiredConfig { get; init; } = [];

    [JsonPropertyName("required_secret_refs")]
    public IReadOnlyList<string> RequiredSecretRefs { get; init; } = [];
}

public sealed record IntegrationPresentationManifest
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; init; }

    [JsonPropertyName("event_types")]
    public IReadOnlyList<string> EventTypes { get; init; } = [];

    [JsonPropertyName("authoring_presets")]
    public IReadOnlyList<JsonElement> AuthoringPresets { get; init; } = [];
}
