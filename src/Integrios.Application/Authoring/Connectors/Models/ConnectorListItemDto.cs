using Integrios.Domain.Entities;

namespace Integrios.Application.Authoring.Connectors;

public sealed record ConnectorListItemDto(
    Guid Id,
    string Key,
    int ContractVersion,
    string Name,
    string Direction,
    string Status,
    string? Description,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public static ConnectorListItemDto From(Connector connector) => new(
        connector.Id,
        connector.Key,
        connector.ContractVersion,
        connector.Name,
        connector.Direction.ToString().ToLowerInvariant(),
        connector.Status.ToString().ToLowerInvariant(),
        connector.Description,
        connector.CreatedAt,
        connector.UpdatedAt);
}
