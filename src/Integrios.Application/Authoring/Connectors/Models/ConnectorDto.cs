using System.Text.Json;
using Integrios.Domain.Entities;
using Integrios.Domain.Enums;
using Integrios.Domain.ValueObjects;

namespace Integrios.Application.Authoring.Connectors;

public sealed record ConnectorDto
{
    public required Guid Id { get; init; }
    public required string Key { get; init; }
    public required int ContractVersion { get; init; }
    public required int ManifestSchemaVersion { get; init; }
    public required string Name { get; init; }
    public required string Direction { get; init; }
    public required IReadOnlyList<string> SupportedAuthSchemes { get; init; }
    public required string Status { get; init; }
    public string? Description { get; init; }
    public required JsonElement Manifest { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }

    public static ConnectorDto From(Connector connector) => new()
    {
        Id = connector.Id,
        Key = connector.Key,
        ContractVersion = connector.ContractVersion,
        ManifestSchemaVersion = connector.ManifestSchemaVersion,
        Name = connector.Name,
        Direction = connector.Direction.ToString().ToLowerInvariant(),
        SupportedAuthSchemes = connector.SupportedAuthSchemes,
        Status = connector.Status.ToString().ToLowerInvariant(),
        Description = connector.Description,
        Manifest = ConnectorManifestParser.ToJson(connector.Manifest),
        CreatedAt = connector.CreatedAt,
        UpdatedAt = connector.UpdatedAt,
    };
}
