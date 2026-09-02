using Integrios.Domain.Entities;

namespace Integrios.Application.Identity;

public interface IOperatorIdentityStore
{
    /// Resolves the issuer and subject pair to its User, creating both the Operator identity and the
    /// User when the pair signs in for the first time. Concurrent first sign-ins for one pair must
    /// resolve to the same User rather than creating a duplicate.
    Task<User> ResolveAsync(
        string issuer,
        string subject,
        OperatorIdentityClaims claims,
        CancellationToken cancellationToken);

    Task<User?> FindByIdAsync(Guid userId, CancellationToken cancellationToken);
}

/// Descriptive provider claims. They update the User's presentation on each sign-in and never
/// establish or link identity.
public sealed record OperatorIdentityClaims(string? DisplayName, string? Email);
