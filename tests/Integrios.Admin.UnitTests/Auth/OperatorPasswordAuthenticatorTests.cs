using Integrios.Admin.Auth;
using Integrios.Application.Identity;
using Microsoft.AspNetCore.Identity;

namespace Integrios.Admin.UnitTests.Auth;

public sealed class OperatorPasswordAuthenticatorTests
{
    [Fact]
    public async Task UnknownEmail_PerformsDummyVerification_AndMatchesWrongPasswordOutcome()
    {
        var store = new StubPasswordAuthenticationStore();
        var hasher = new RecordingPasswordHasher();
        var authenticator = new OperatorPasswordAuthenticator(store, hasher);

        OperatorPasswordAuthentication? unknown = await authenticator.AuthenticateAsync(
            "unknown@example.com",
            "fifteen-letters!",
            CancellationToken.None);
        unknown.ShouldBeNull();
        hasher.VerifiedHashes.Count.ShouldBe(1);
        string dummyHash = hasher.VerifiedHashes[0];
        dummyHash.ShouldNotBeNullOrWhiteSpace();

        store.Credential = new PasswordAuthenticationCredential(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Operator",
            "stored-hash",
            1);
        OperatorPasswordAuthentication? wrong = await authenticator.AuthenticateAsync(
            "known@example.com",
            "fifteen-letters!",
            CancellationToken.None);
        wrong.ShouldBeNull();
        hasher.VerifiedHashes.ShouldBe([dummyHash, "stored-hash"]);
        store.RecordedUserIds.ShouldBeEmpty();
    }

    private sealed class RecordingPasswordHasher : IPasswordHasher<string>
    {
        public List<string> VerifiedHashes { get; } = [];

        public string HashPassword(string user, string password) => "replacement";

        public PasswordVerificationResult VerifyHashedPassword(
            string user,
            string hashedPassword,
            string providedPassword)
        {
            VerifiedHashes.Add(hashedPassword);
            return PasswordVerificationResult.Failed;
        }
    }

    private sealed class StubPasswordAuthenticationStore : IPasswordAuthenticationStore
    {
        public PasswordAuthenticationCredential? Credential { get; set; }
        public List<Guid> RecordedUserIds { get; } = [];

        public Task<PasswordAuthenticationCredential?> FindEnabledAsync(
            string normalizedEmail,
            CancellationToken cancellationToken) => Task.FromResult(Credential);

        public Task<bool> IsCurrentSessionAsync(
            Guid credentialId,
            Guid userId,
            int sessionRevision,
            CancellationToken cancellationToken) => Task.FromResult(false);

        public Task RecordSuccessfulSignInAsync(Guid userId, CancellationToken cancellationToken)
        {
            RecordedUserIds.Add(userId);
            return Task.CompletedTask;
        }

        public Task ReplaceHashAsync(
            Guid credentialId,
            string currentHash,
            string replacementHash,
            DateTimeOffset changedAt,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
