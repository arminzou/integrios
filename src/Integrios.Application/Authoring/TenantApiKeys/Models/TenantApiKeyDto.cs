using Integrios.Domain.Entities;
using Integrios.Domain.ValueObjects;

namespace Integrios.Application.Authoring.TenantApiKeys;

public sealed record TenantApiKeyDto
{
    public required Guid Id { get; init; }
    public required Guid TenantId { get; init; }
    public required string Name { get; init; }
    public required string KeyPrefix { get; init; }
    public required string State { get; init; }
    public string? Description { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? LastUsedAt { get; init; }
    public DateTimeOffset? RevokedAt { get; init; }

    public static TenantApiKeyDto From(TenantApiKey key) => new()
    {
        Id = key.Id,
        TenantId = key.TenantId,
        Name = key.Name,
        KeyPrefix = key.KeyPrefix,
        State = TenantApiKeyState.From(key),
        Description = key.Description,
        CreatedAt = key.CreatedAt,
        LastUsedAt = key.LastUsedAt,
        RevokedAt = key.RevokedAt,
    };
}
