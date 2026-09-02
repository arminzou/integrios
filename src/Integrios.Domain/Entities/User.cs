namespace Integrios.Domain.Entities;

/// The persisted, provider-neutral human principal for one member of the Operator. A User owns no
/// local credential, no Role, and no Tenant authority; DisplayName and Email are descriptive claims
/// captured from the provider and never establish identity.
public sealed record User
{
    public required Guid Id { get; init; }
    public required string DisplayName { get; init; }
    public string? Email { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? LastSignedInAt { get; init; }
}
