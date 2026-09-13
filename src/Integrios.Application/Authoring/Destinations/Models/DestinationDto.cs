using System.Text.Json;
using Integrios.Domain.Entities;

namespace Integrios.Application.Authoring.Destinations;

public sealed record DestinationDto
{
    public required Guid Id { get; init; }
    public required Guid TenantId { get; init; }
    public required Guid ConnectorId { get; init; }
    public required string Name { get; init; }
    public required JsonElement Configuration { get; init; }
    public DestinationAuthenticationDto? Authentication { get; init; }
    public required string Status { get; init; }
    public string? Environment { get; init; }
    public string? Description { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }

    public static DestinationDto From(Destination destination) => new()
    {
        Id = destination.Id,
        TenantId = destination.TenantId,
        ConnectorId = destination.ConnectorId,
        Name = destination.Name,
        Configuration = destination.Configuration,
        Authentication = DestinationAuthenticationDto.From(destination.Authentication),
        Status = destination.Status.ToString().ToLowerInvariant(),
        Environment = destination.Environment,
        Description = destination.Description,
        CreatedAt = destination.CreatedAt,
        UpdatedAt = destination.UpdatedAt,
    };
}
