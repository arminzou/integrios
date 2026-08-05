using Integrios.Domain.Tenants;

namespace Integrios.Application.ApiKeys;

public sealed record ApiKeyDto
{
    public required Guid Id { get; init; }
    public required Guid TenantId { get; init; }
    public required string Name { get; init; }
    public required string KeyPrefix { get; init; }
    public required string Status { get; init; }
    public string? Description { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public DateTimeOffset? LastUsedAt { get; init; }

    public static ApiKeyDto From(ApiKey key) => new()
    {
        Id = key.Id,
        TenantId = key.TenantId,
        Name = key.Name,
        KeyPrefix = key.KeyPrefix,
        Status = key.Status.ToString().ToLowerInvariant(),
        Description = key.Description,
        CreatedAt = key.CreatedAt,
        ExpiresAt = key.ExpiresAt,
        LastUsedAt = key.LastUsedAt,
    };
}
