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
        var verifiedHashes = new List<string>();
        var authenticator = new OperatorPasswordAuthenticator(
            store,
            "dummy-hash",
            (hash, _) =>
            {
                verifiedHashes.Add(hash);
                return PasswordVerificationResult.Failed;
            },
            _ => "replacement");

        OperatorPasswordAuthentication? unknown = await authenticator.AuthenticateAsync(
            "unknown@example.com",
            "fifteen-letters!",
            CancellationToken.None);
        unknown.ShouldBeNull();
        verifiedHashes.ShouldBe(["dummy-hash"]);

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
        verifiedHashes.ShouldBe(["dummy-hash", "stored-hash"]);
        store.RecordedUserIds.ShouldBeEmpty();
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
