using Integrios.Domain.Enums;
using Integrios.Domain.ValueObjects;

namespace Integrios.Domain.Entities;

public sealed record Connector
{
    public required Guid Id { get; init; }
    public required string Key { get; init; }
    public required int ContractVersion { get; init; }
    public required int ManifestSchemaVersion { get; init; }
    public required string Name { get; init; }
    public required ConnectorDirection Direction { get; init; }
    public required IReadOnlyList<string> SupportedAuthSchemes { get; init; }
    public required OperationalStatus Status { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
    public string? Description { get; init; }
    public required ConnectorManifest Manifest { get; init; }
}
