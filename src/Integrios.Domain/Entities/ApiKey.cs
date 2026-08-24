using Integrios.Domain.Enums;

namespace Integrios.Domain.Entities;

public sealed record ApiKey
{
    public required Guid Id { get; init; }
    public required Guid TenantId { get; init; }
    public required string Name { get; init; }
    public required string KeyPrefix { get; init; }
    public required string KeyHash { get; init; }
    public required OperationalStatus Status { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public DateTimeOffset? LastUsedAt { get; init; }
    public DateTimeOffset? RevokedAt { get; init; }
    public string? Description { get; init; }
}
