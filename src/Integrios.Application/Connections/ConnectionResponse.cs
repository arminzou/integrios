using System.Text.Json;
using Integrios.Domain.Integrations;

namespace Integrios.Application.Connections;

public sealed record ConnectionResponse
{
    public required Guid Id { get; init; }
    public required Guid TenantId { get; init; }
    public required Guid IntegrationId { get; init; }
    public required string Name { get; init; }
    public required JsonElement Config { get; init; }
    public required string Status { get; init; }
    public string? Environment { get; init; }
    public string? Description { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }

    public static ConnectionResponse From(Connection c) => new()
    {
        Id = c.Id,
        TenantId = c.TenantId,
        IntegrationId = c.IntegrationId,
        Name = c.Name,
        Config = c.Config,
        Status = c.Status.ToString().ToLowerInvariant(),
        Environment = c.Environment,
        Description = c.Description,
        CreatedAt = c.CreatedAt,
        UpdatedAt = c.UpdatedAt,
    };
}

public sealed record ConnectionListResponse
{
    public required IReadOnlyList<ConnectionResponse> Items { get; init; }
    public string? NextCursor { get; init; }
}
