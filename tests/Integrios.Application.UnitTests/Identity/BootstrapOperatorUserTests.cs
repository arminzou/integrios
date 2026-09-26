using Integrios.Application.Identity;
using Integrios.Domain.Entities;
using NSubstitute;

namespace Integrios.Application.UnitTests.Identity;

public sealed class BootstrapOperatorUserTests
{
    [Fact]
    public async Task ExistingUser_IgnoresAbsentInputAndDoesNotWrite()
    {
        var lifecycle = Substitute.For<IPasswordCredentialLifecycle>();
        lifecycle.IsInitializedAsync(CancellationToken.None).Returns(true);
        var handler = new BootstrapOperatorUserCommandHandler(lifecycle);

        (await handler.Handle(new(null, null, null), CancellationToken.None)).ShouldBeFalse();

        await lifecycle.DidNotReceive().CreateFirstAsync(
            Arg.Any<User>(), Arg.Any<PasswordCredential>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(null, "operator@example.com", "hash")]
    [InlineData("Operator", "bad-email", "hash")]
    [InlineData("Operator", "operator@example.com", null)]
    public async Task FreshSetup_RejectsInvalidInputsBeforeWriting(string? name, string? email, string? hash)
    {
        var lifecycle = Substitute.For<IPasswordCredentialLifecycle>();
        var handler = new BootstrapOperatorUserCommandHandler(lifecycle);

        await Should.ThrowAsync<ArgumentException>(() => handler.Handle(new(name, email, hash), CancellationToken.None));

        await lifecycle.DidNotReceive().CreateFirstAsync(
            Arg.Any<User>(), Arg.Any<PasswordCredential>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FreshSetup_NormalizesEmailAndReturnsConcurrentLoserOutcome()
    {
        var lifecycle = Substitute.For<IPasswordCredentialLifecycle>();
        var handler = new BootstrapOperatorUserCommandHandler(lifecycle);

        (await handler.Handle(new(" Operator ", " Operator@Example.com ", "hash"), CancellationToken.None)).ShouldBeFalse();

        await lifecycle.Received(1).CreateFirstAsync(
            Arg.Is<User>(user => user.DisplayName == "Operator" && user.Email == null),
            Arg.Is<PasswordCredential>(credential => credential.NormalizedEmail == "OPERATOR@EXAMPLE.COM"
                && credential.PasswordHash == "hash" && credential.SessionRevision == 1 && credential.DisabledAt == null),
            CancellationToken.None);
    }
}
