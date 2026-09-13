namespace Integrios.Application.Identity;

public interface IPasswordAuthenticationStore
{
    Task<PasswordAuthenticationCredential?> FindEnabledAsync(
        string normalizedEmail,
        CancellationToken cancellationToken);

    Task<bool> IsCurrentSessionAsync(
        Guid credentialId,
        Guid userId,
        int sessionRevision,
        CancellationToken cancellationToken);

    Task RecordSuccessfulSignInAsync(Guid userId, CancellationToken cancellationToken);

    Task ReplaceHashAsync(
        Guid credentialId,
        string currentHash,
        string replacementHash,
        DateTimeOffset changedAt,
        CancellationToken cancellationToken);
}

public sealed record PasswordAuthenticationCredential(
    Guid CredentialId,
    Guid UserId,
    string DisplayName,
    string PasswordHash,
    int SessionRevision);
