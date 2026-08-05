using System.Text.Json;
using System.Text.Json.Serialization;

namespace Integrios.Domain.Integrations;

public sealed record IntegrationManifest
{
    public required int ManifestSchemaVersion { get; init; }

    public required string Key { get; init; }

    public required int ContractVersion { get; init; }

    public required string Direction { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? SourceConfigurationSchema { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? DestinationConfigurationSchema { get; init; }

    public required IntegrationSourceVerificationManifest SourceVerification { get; init; }

    public required IntegrationDestinationAuthenticationManifest DestinationAuthentication { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IntegrationSourceAdapterManifest? SourceAdapter { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? HttpOutcome { get; init; }

    public required IntegrationPresentationManifest Presentation { get; init; }
}

public sealed record IntegrationSourceVerificationManifest
{
    public required bool AllowUnverified { get; init; }

    public IReadOnlyList<IntegrationSchemeManifest> Schemes { get; init; } = [];
}

public sealed record IntegrationDestinationAuthenticationManifest
{
    public required bool AllowUnauthenticated { get; init; }

    public IReadOnlyList<IntegrationSchemeManifest> Schemes { get; init; } = [];
}

public sealed record IntegrationSourceAdapterManifest
{
    public required string Key { get; init; }

    public required int ContractVersion { get; init; }

    public JsonElement Config { get; init; }
}

public sealed record IntegrationSchemeManifest
{
    public required string Scheme { get; init; }

    public IReadOnlyList<string> RequiredConfig { get; init; } = [];

    public IReadOnlyList<string> RequiredSecretRefs { get; init; } = [];
}

public sealed record IntegrationPresentationManifest
{
    public required string Name { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; init; }

    public IReadOnlyList<string> EventTypes { get; init; } = [];

    public IReadOnlyList<JsonElement> AuthoringPresets { get; init; } = [];
}
