using Integrios.Domain.Entities;

namespace Integrios.Application.Authoring.Connections;

public sealed record ConnectionListItemDto(
    Guid Id,
    Guid TenantId,
    Guid ConnectorId,
    // The Connector as an Operator names it, resolved where the row is read rather than left for
    // the caller to look up: an identifier answers a different question than "what is this built
    // from", and every caller would otherwise repeat the same join.
    string ConnectorKey,
    string Name,
    string Status,
    string? Environment,
    string? Description,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public static ConnectionListItemDto From(Connection connection, string connectorKey) => new(
        connection.Id,
        connection.TenantId,
        connection.ConnectorId,
        connectorKey,
        connection.Name,
        connection.Status.ToString().ToLowerInvariant(),
        connection.Environment,
        connection.Description,
        connection.CreatedAt,
        connection.UpdatedAt);
}
