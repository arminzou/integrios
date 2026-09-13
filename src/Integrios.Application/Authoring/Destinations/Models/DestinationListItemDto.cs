using Integrios.Domain.Entities;

namespace Integrios.Application.Authoring.Destinations;

public sealed record DestinationListItemDto(
    Guid Id,
    Guid TenantId,
    Guid ConnectorId,
    string ConnectorKey,
    string Name,
    string Status,
    string? Environment,
    string? Description,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public static DestinationListItemDto From(Destination destination, string connectorKey) => new(
        destination.Id,
        destination.TenantId,
        destination.ConnectorId,
        connectorKey,
        destination.Name,
        destination.Status.ToString().ToLowerInvariant(),
        destination.Environment,
        destination.Description,
        destination.CreatedAt,
        destination.UpdatedAt);
}
