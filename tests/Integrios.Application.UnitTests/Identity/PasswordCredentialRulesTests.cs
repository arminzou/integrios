using Integrios.Application.Identity;

namespace Integrios.Application.UnitTests.Identity;

public sealed class PasswordCredentialRulesTests
{
    [Fact]
    public void TryNormalizeEmail_TrimsAndUsesInvariantUppercase()
    {
        bool valid = PasswordCredentialRules.TryNormalizeEmail(
            "  Operator.Example+Alerts@example.com  ",
            out string email,
            out string normalizedEmail);

        valid.ShouldBeTrue();
        email.ShouldBe("Operator.Example+Alerts@example.com");
        normalizedEmail.ShouldBe("OPERATOR.EXAMPLE+ALERTS@EXAMPLE.COM");
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-an-email")]
    [InlineData("Operator <operator@example.com>")]
    public void TryNormalizeEmail_RejectsNonAddresses(string value)
    {
        PasswordCredentialRules.TryNormalizeEmail(value, out _, out _).ShouldBeFalse();
    }

    [Fact]
    public void IsValidPassword_CountsUnicodeScalars()
    {
        PasswordCredentialRules.IsValidPassword(new string('a', 14)).ShouldBeFalse();
        PasswordCredentialRules.IsValidPassword(new string('a', 15)).ShouldBeTrue();
        PasswordCredentialRules.IsValidPassword(string.Concat(Enumerable.Repeat("😀", 128))).ShouldBeTrue();
        PasswordCredentialRules.IsValidPassword(string.Concat(Enumerable.Repeat("😀", 129))).ShouldBeFalse();
        PasswordCredentialRules.IsValidPassword(new string('\ud800', 15)).ShouldBeFalse();
    }

    [Fact]
    public void IsValidPasswordHash_RequiresBoundedNonWhitespaceValue()
    {
        PasswordCredentialRules.IsValidPasswordHash("versioned-hash").ShouldBeTrue();
        PasswordCredentialRules.IsValidPasswordHash("").ShouldBeFalse();
        PasswordCredentialRules.IsValidPasswordHash("   ").ShouldBeFalse();
        PasswordCredentialRules.IsValidPasswordHash(new string('x', 1025)).ShouldBeFalse();
    }
}
