using Integrios.Domain.Common;

namespace Integrios.Domain.Integrations;

public sealed record Integration
{
    public required Guid Id { get; init; }
    public required string Key { get; init; }
    public required int ContractVersion { get; init; }
    public required int ManifestSchemaVersion { get; init; }
    public required string Name { get; init; }
    public required IntegrationDirection Direction { get; init; }
    public required IReadOnlyList<string> SupportedAuthSchemes { get; init; }
    public required OperationalStatus Status { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
    public string? Description { get; init; }
    public required IntegrationManifest Manifest { get; init; }
}
