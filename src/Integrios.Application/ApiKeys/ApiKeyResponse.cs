using Integrios.Domain.Tenants;

namespace Integrios.Application.ApiKeys;

public sealed record ApiKeyResponse
{
    public required Guid Id { get; init; }
    public required Guid TenantId { get; init; }
    public required string Name { get; init; }
    public required string PublicKey { get; init; }
    public required string Status { get; init; }
    public required IReadOnlyList<string> Scopes { get; init; }
    public string? Description { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public DateTimeOffset? LastUsedAt { get; init; }

    public static ApiKeyResponse From(ApiKey key) => new()
    {
        Id = key.Id,
        TenantId = key.TenantId,
        Name = key.Name,
        PublicKey = key.PublicKey,
        Status = key.Status.ToString().ToLowerInvariant(),
        Scopes = key.Scopes,
        Description = key.Description,
        CreatedAt = key.CreatedAt,
        ExpiresAt = key.ExpiresAt,
        LastUsedAt = key.LastUsedAt,
    };
}

// Returned only on create — carries the plaintext secret once.
public sealed record CreateApiKeyResponse
{
    public required ApiKeyResponse Key { get; init; }
    public required string Secret { get; init; }
}

public sealed record ApiKeyListResponse
{
    public required IReadOnlyList<ApiKeyResponse> Items { get; init; }
    public string? NextCursor { get; init; }
}
