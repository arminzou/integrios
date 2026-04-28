using Integrios.Domain.Integrations;

namespace Integrios.Application.Integrations;

public sealed record IntegrationResponse
{
    public required Guid Id { get; init; }
    public required string Key { get; init; }
    public required string Name { get; init; }
    public required string Direction { get; init; }
    public required string AuthScheme { get; init; }
    public required string Status { get; init; }
    public string? Description { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }

    public static IntegrationResponse From(Integration i) => new()
    {
        Id = i.Id,
        Key = i.Key,
        Name = i.Name,
        Direction = i.Direction.ToString().ToLowerInvariant(),
        AuthScheme = i.AuthScheme.ToString().ToLowerInvariant(),
        Status = i.Status.ToString().ToLowerInvariant(),
        Description = i.Description,
        CreatedAt = i.CreatedAt,
        UpdatedAt = i.UpdatedAt,
    };
}

public sealed record IntegrationListResponse
{
    public required IReadOnlyList<IntegrationResponse> Items { get; init; }
    public string? NextCursor { get; init; }
}
