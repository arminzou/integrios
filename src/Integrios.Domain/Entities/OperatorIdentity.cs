namespace Integrios.Domain.Entities;

/// The persisted mapping from one provider-qualified OpenID Connect issuer and subject to exactly
/// one User. The pair is the only identity authority: email equality never links identities.
public sealed record OperatorIdentity
{
    public required Guid Id { get; init; }
    public required Guid UserId { get; init; }
    public required string Issuer { get; init; }
    public required string Subject { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
}
