using Integrios.Domain.Entities;

namespace Integrios.Application.Authoring.Connections;

public sealed record ConnectionListItemDto(
    Guid Id,
    Guid TenantId,
    Guid ConnectorId,
    string Name,
    string Status,
    string? Environment,
    string? Description,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public static ConnectionListItemDto From(Connection connection) => new(
        connection.Id,
        connection.TenantId,
        connection.ConnectorId,
        connection.Name,
        connection.Status.ToString().ToLowerInvariant(),
        connection.Environment,
        connection.Description,
        connection.CreatedAt,
        connection.UpdatedAt);
}
