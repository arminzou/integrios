using Integrios.Core.Domain.Common;

namespace Integrios.Core.Domain.Tenants;

public sealed record ApiKey
{
    public required Guid Id { get; init; }
    public required Guid TenantId { get; init; }
    public required string Name { get; init; }
    public required string PublicKey { get; init; }
    public required string SecretHash { get; init; }
    public required IReadOnlyList<string> Scopes { get; init; }
    public required OperationalStatus Status { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public DateTimeOffset? LastUsedAt { get; init; }
    public string? Description { get; init; }
}
