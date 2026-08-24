using System.Text.Json;
using Integrios.Domain.Entities;
using Integrios.Domain.ValueObjects;

namespace Integrios.Application.Authoring.Connections;

public sealed record ConnectionDto
{
    public required Guid Id { get; init; }
    public required Guid TenantId { get; init; }
    public required Guid ConnectorId { get; init; }
    public required string Name { get; init; }
    public required JsonElement Config { get; init; }
    public SourceVerificationDto? SourceVerification { get; init; }
    public DestinationAuthenticationDto? DestinationAuthentication { get; init; }
    public required string Status { get; init; }
    public string? Environment { get; init; }
    public string? Description { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }

    public static ConnectionDto From(Connection connection) => new()
    {
        Id = connection.Id,
        TenantId = connection.TenantId,
        ConnectorId = connection.ConnectorId,
        Name = connection.Name,
        Config = connection.Config,
        SourceVerification = SourceVerificationDto.From(connection.SourceVerification),
        DestinationAuthentication = DestinationAuthenticationDto.From(connection.DestinationAuthentication),
        Status = connection.Status.ToString().ToLowerInvariant(),
        Environment = connection.Environment,
        Description = connection.Description,
        CreatedAt = connection.CreatedAt,
        UpdatedAt = connection.UpdatedAt,
    };
}
