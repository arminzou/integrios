using System.Text.Json;
using System.Text.Json.Serialization;
using Integrios.Domain.Integrations;

namespace Integrios.Application.Connections;

public sealed record ConnectionResponse
{
    public required Guid Id { get; init; }
    public required Guid TenantId { get; init; }
    public required Guid IntegrationId { get; init; }
    public required string Name { get; init; }
    public required JsonElement Config { get; init; }
    [JsonPropertyName("source_verification")]
    public ConnectionSchemeSelectionResponse? SourceVerification { get; init; }
    [JsonPropertyName("destination_authentication")]
    public ConnectionSchemeSelectionResponse? DestinationAuthentication { get; init; }
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
        SourceVerification = ConnectionSchemeSelectionResponse.From(connection.SourceVerification),
        DestinationAuthentication = ConnectionSchemeSelectionResponse.From(connection.DestinationAuthentication),
        Status = connection.Status.ToString().ToLowerInvariant(),
        Environment = connection.Environment,
        Description = connection.Description,
        CreatedAt = connection.CreatedAt,
        UpdatedAt = connection.UpdatedAt,
    };
}

public sealed record ConnectionSchemeSelectionResponse
{
    public required string Scheme { get; init; }
    public required JsonElement Config { get; init; }

    public static ConnectionSchemeSelectionResponse? From(ConnectionSchemeSelection? selection) =>
        selection is null
            ? null
            : new ConnectionSchemeSelectionResponse
            {
                Scheme = selection.Scheme,
                Config = selection.Config
            };
}

public sealed record ConnectionListResponse
{
    public required IReadOnlyList<ConnectionResponse> Items { get; init; }
    public string? NextCursor { get; init; }
}
