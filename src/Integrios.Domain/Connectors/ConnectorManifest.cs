using System.Text.Json;
using System.Text.Json.Serialization;

namespace Integrios.Domain.Connectors;

public sealed record ConnectorManifest
{
    public required int ManifestSchemaVersion { get; init; }

    public required string Key { get; init; }

    public required int ContractVersion { get; init; }

    public required string Direction { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? SourceConfigurationSchema { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? DestinationConfigurationSchema { get; init; }

    public required ConnectorSourceVerificationManifest SourceVerification { get; init; }

    public required ConnectorDestinationAuthenticationManifest DestinationAuthentication { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ConnectorSourceAdapterManifest? SourceAdapter { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? HttpOutcome { get; init; }

    public required ConnectorPresentationManifest Presentation { get; init; }
}

public sealed record ConnectorSourceVerificationManifest
{
    public required bool AllowUnverified { get; init; }

    public IReadOnlyList<ConnectorSchemeManifest> Schemes { get; init; } = [];
}

public sealed record ConnectorDestinationAuthenticationManifest
{
    public required bool AllowUnauthenticated { get; init; }

    public IReadOnlyList<ConnectorSchemeManifest> Schemes { get; init; } = [];
}

public sealed record ConnectorSourceAdapterManifest
{
    public required string Key { get; init; }

    public required int ContractVersion { get; init; }

    public JsonElement Config { get; init; }
}

public sealed record ConnectorSchemeManifest
{
    public required string Scheme { get; init; }

    public IReadOnlyList<string> RequiredConfig { get; init; } = [];

    public IReadOnlyList<string> RequiredSecretRefs { get; init; } = [];
}

public sealed record ConnectorPresentationManifest
{
    public required string Name { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; init; }

    public IReadOnlyList<string> EventTypes { get; init; } = [];

    public IReadOnlyList<JsonElement> AuthoringPresets { get; init; } = [];
}
