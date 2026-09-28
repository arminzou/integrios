using Integrios.Domain.Entities;

namespace Integrios.Application.Identity;

public interface IPasswordCredentialLifecycle
{
    Task<PasswordCredentialMutationStatus> CreateAsync(
        User user,
        PasswordCredential credential,
        CancellationToken cancellationToken);

    Task<PasswordCredentialMutationStatus> SetPasswordAsync(
        Guid userId,
        string? email,
        string? normalizedEmail,
        string passwordHash,
        DateTimeOffset changedAt,
        CancellationToken cancellationToken);

    Task<PasswordCredentialMutationStatus> ChangeEmailAsync(
        Guid userId,
        string email,
        string normalizedEmail,
        DateTimeOffset changedAt,
        CancellationToken cancellationToken);

    Task<PasswordCredentialMutationStatus> DisableAsync(
        Guid userId,
        DateTimeOffset disabledAt,
        CancellationToken cancellationToken);
}

public enum PasswordCredentialMutationStatus
{
    Succeeded,
    UserNotFound,
    CredentialNotFound,
    EmailRequired,
    EmailNotAllowed,
    EmailConflict,
    ConcurrentConflict,
}

public sealed record PasswordCredentialMutationResult(
    PasswordCredentialMutationStatus Status,
    Guid UserId);
