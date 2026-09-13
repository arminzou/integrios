namespace Integrios.Domain.Entities;

/// The persisted, provider-neutral human principal for one member of the Operator. A User owns no
/// Tenant authority. A User may own one Password credential; DisplayName and Email remain
/// descriptive claims and never establish identity.
public sealed record User
{
    public required Guid Id { get; init; }
    public required string DisplayName { get; init; }
    public string? Email { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? LastSignedInAt { get; init; }
}
