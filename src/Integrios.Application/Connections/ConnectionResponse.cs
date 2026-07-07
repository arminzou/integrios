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
    public ConnectionAuthResponse? Auth { get; init; }
    public required string Status { get; init; }
    public string? Environment { get; init; }
    public string? Description { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }

    public static ConnectionResponse From(Connection connection) => new()
    {
        Id = connection.Id,
        TenantId = connection.TenantId,
        IntegrationId = connection.IntegrationId,
        Name = connection.Name,
        Config = connection.Config,
        Auth = ConnectionAuthResponse.From(connection.Auth),
        Status = connection.Status.ToString().ToLowerInvariant(),
        Environment = connection.Environment,
        Description = connection.Description,
        CreatedAt = connection.CreatedAt,
        UpdatedAt = connection.UpdatedAt,
    };
}

public sealed record ConnectionAuthResponse
{
    public required string Scheme { get; init; }
    public required JsonElement Config { get; init; }

    public static ConnectionAuthResponse? From(ConnectionAuth? auth) =>
        auth is null
            ? null
            : new ConnectionAuthResponse
            {
                Scheme = auth.Scheme,
                Config = auth.Config
            };
}

public sealed record ConnectionListResponse
{
    public required IReadOnlyList<ConnectionResponse> Items { get; init; }
    public string? NextCursor { get; init; }
}
