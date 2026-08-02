using System.Text.Json;
using Integrios.Domain.Integrations;

namespace Integrios.Application.Integrations;

public sealed record IntegrationResponse
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

    public static IntegrationResponse From(Integration integration) => new()
    {
        Id = integration.Id,
        Key = integration.Key,
        ContractVersion = integration.ContractVersion,
        ManifestSchemaVersion = integration.ManifestSchemaVersion,
        Name = integration.Name,
        Direction = integration.Direction.ToString().ToLowerInvariant(),
        SupportedAuthSchemes = integration.SupportedAuthSchemes,
        Status = integration.Status.ToString().ToLowerInvariant(),
        Description = integration.Description,
        Manifest = IntegrationManifestParser.ToJson(integration.Manifest),
        CreatedAt = integration.CreatedAt,
        UpdatedAt = integration.UpdatedAt,
    };
}

public sealed record IntegrationListResponse
{
    public required IReadOnlyList<IntegrationResponse> Items { get; init; }
    public string? NextCursor { get; init; }
}
