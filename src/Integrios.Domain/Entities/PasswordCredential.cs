namespace Integrios.Domain.Entities;

/// The single Integrios-managed email-and-password sign-in method a User may own. Its normalized
/// email identifies this credential but never links an Operator identity.
public sealed record PasswordCredential
{
    public required Guid Id { get; init; }
    public required Guid UserId { get; init; }
    public required string Email { get; init; }
    public required string NormalizedEmail { get; init; }
    public required string PasswordHash { get; init; }
    public required int SessionRevision { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
    public DateTimeOffset? DisabledAt { get; init; }
}
