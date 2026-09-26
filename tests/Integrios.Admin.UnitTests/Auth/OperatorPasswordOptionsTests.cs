using Integrios.Admin.Auth;
using Microsoft.Extensions.Configuration;

namespace Integrios.Admin.UnitTests.Auth;

public sealed class OperatorPasswordOptionsTests
{
    [Fact]
    public void PasswordLogin_IsEnabledByDefault()
    {
        IConfiguration configuration = new ConfigurationBuilder().Build();

        OperatorPasswordOptions.FromConfiguration(configuration).Enabled.ShouldBeTrue();
        OperatorAuthentication.IsHumanAuthenticationConfigured(configuration).ShouldBeTrue();
        OperatorAuthentication.IsOidcConfigured(configuration).ShouldBeFalse();
    }

    [Fact]
    public void PasswordLogin_CanExplicitlyDisableHumanAuthentication()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [OperatorPasswordOptions.EnabledKey] = "false",
            })
            .Build();

        OperatorPasswordOptions.FromConfiguration(configuration).Enabled.ShouldBeFalse();
        OperatorAuthentication.IsHumanAuthenticationConfigured(configuration).ShouldBeFalse();
        OperatorAuthentication.IsOidcConfigured(configuration).ShouldBeFalse();
    }
}
