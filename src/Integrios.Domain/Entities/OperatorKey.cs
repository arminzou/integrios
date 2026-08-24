namespace Integrios.Domain.Entities;

public sealed record OperatorKey
{
    public required Guid Id { get; init; }
    public required string PublicKey { get; init; }
    public required string SecretHash { get; init; }
    public required string Name { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? RevokedAt { get; init; }
}
