using Integrios.Application.Identity;
using Microsoft.AspNetCore.Identity;

namespace Integrios.Admin.Auth;

internal sealed class OperatorPasswordAuthenticator
{
    private readonly IPasswordAuthenticationStore store;
    private static readonly string DummyHash = new PasswordHasher<string>().HashPassword(
        string.Empty,
        "dummy-password-never-used");
    private readonly IPasswordHasher<string> hasher;

    public OperatorPasswordAuthenticator(
        IPasswordAuthenticationStore store,
        IPasswordHasher<string> hasher)
    {
        this.store = store;
        this.hasher = hasher;
    }

    public async Task<OperatorPasswordAuthentication?> AuthenticateAsync(
        string? enteredEmail,
        string? password,
        CancellationToken cancellationToken)
    {
        if (!PasswordCredentialRules.TryNormalizeEmail(
                enteredEmail,
                out _,
                out string normalizedEmail)
            || password is null
            || !PasswordCredentialRules.IsValidPassword(password))
        {
            hasher.VerifyHashedPassword(string.Empty, DummyHash, password ?? string.Empty);
            return null;
        }

        PasswordAuthenticationCredential? credential = await store.FindEnabledAsync(
            normalizedEmail,
            cancellationToken);
        if (credential is null)
        {
            hasher.VerifyHashedPassword(string.Empty, DummyHash, password);
            return null;
        }

        PasswordVerificationResult verification = hasher.VerifyHashedPassword(
            string.Empty,
            credential.PasswordHash,
            password);
        if (verification == PasswordVerificationResult.Failed)
            return null;

        if (verification == PasswordVerificationResult.SuccessRehashNeeded)
        {
            await store.ReplaceHashAsync(
                credential.CredentialId,
                credential.PasswordHash,
                hasher.HashPassword(string.Empty, password),
                DateTimeOffset.UtcNow,
                cancellationToken);
        }

        await store.RecordSuccessfulSignInAsync(credential.UserId, cancellationToken);
        return new OperatorPasswordAuthentication(
            credential.CredentialId,
            credential.UserId,
            credential.DisplayName,
            credential.SessionRevision);
    }
}

internal sealed record OperatorPasswordAuthentication(
    Guid CredentialId,
    Guid UserId,
    string DisplayName,
    int SessionRevision);
