using Integrios.Application.Identity;
using Microsoft.AspNetCore.Identity;

namespace Integrios.Admin.Auth;

internal sealed class OperatorPasswordAuthenticator
{
    private readonly IPasswordAuthenticationStore store;
    private static readonly PasswordHasher<string> Hasher = new();
    private static readonly string DummyHash = Hasher.HashPassword(
        string.Empty,
        "dummy-password-never-used");
    private readonly string dummyHash;
    private readonly Func<string, string, PasswordVerificationResult> verify;
    private readonly Func<string, string> hash;

    public OperatorPasswordAuthenticator(IPasswordAuthenticationStore store)
    {
        this.store = store;
        dummyHash = DummyHash;
        verify = (storedHash, password) => Hasher.VerifyHashedPassword(
            string.Empty,
            storedHash,
            password);
        hash = password => Hasher.HashPassword(string.Empty, password);
    }

    internal OperatorPasswordAuthenticator(
        IPasswordAuthenticationStore store,
        string dummyHash,
        Func<string, string, PasswordVerificationResult> verify,
        Func<string, string> hash)
    {
        this.store = store;
        this.dummyHash = dummyHash;
        this.verify = verify;
        this.hash = hash;
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
            verify(dummyHash, password ?? string.Empty);
            return null;
        }

        PasswordAuthenticationCredential? credential = await store.FindEnabledAsync(
            normalizedEmail,
            cancellationToken);
        if (credential is null)
        {
            verify(dummyHash, password);
            return null;
        }

        PasswordVerificationResult verification = verify(credential.PasswordHash, password);
        if (verification == PasswordVerificationResult.Failed)
            return null;

        if (verification == PasswordVerificationResult.SuccessRehashNeeded)
        {
            await store.ReplaceHashAsync(
                credential.CredentialId,
                credential.PasswordHash,
                hash(password),
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
