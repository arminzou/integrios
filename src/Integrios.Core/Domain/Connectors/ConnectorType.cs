using System.Text.Json;
using Integrios.Core.Domain.Common;

namespace Integrios.Core.Domain.Connectors;

public sealed record ConnectorType
{
    public required Guid Id { get; init; }
    public required string Key { get; init; }
    public required string Name { get; init; }
    public required ConnectorDirection Direction { get; init; }
    public required ConnectorAuthScheme AuthScheme { get; init; }
    public required JsonElement ConfigSchema { get; init; }
    public required JsonElement SecretSchema { get; init; }
    public required IReadOnlyList<string> Capabilities { get; init; }
    public required OperationalStatus Status { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
    public string? Description { get; init; }
}
