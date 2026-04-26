namespace Integrios.Domain.Tenants;

public sealed record AdminKey
{
    public required Guid Id { get; init; }
    public Guid? TenantId { get; init; }  // null = global
    public required string PublicKey { get; init; }
    public required string SecretHash { get; init; }
    public required string Name { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? RevokedAt { get; init; }

    public bool IsGlobal => TenantId is null;
}
