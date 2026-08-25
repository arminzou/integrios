using Integrios.Application.Delivery;

namespace Integrios.Application.UnitTests;

public sealed class HttpTargetComposerTests
{
    [Theory]
    [InlineData("https://slack.com/api", "chat.postMessage", "https://slack.com/api/chat.postMessage")]
    [InlineData("https://slack.com/api/", "/chat.postMessage", "https://slack.com/api/chat.postMessage")]
    [InlineData("https://slack.com/api///", "/chat.postMessage", "https://slack.com/api/chat.postMessage")]
    [InlineData("https://dsg.crm.test/api/data/v9.2", "/contacts?$select=fullname,emailaddress1", "https://dsg.crm.test/api/data/v9.2/contacts?$select=fullname,emailaddress1")]
    [InlineData("https://dsg.crm.test/api/data/v9.2", "?$select=fullname", "https://dsg.crm.test/api/data/v9.2?$select=fullname")]
    public void Compose_AppendsToThePreservedBasePath(
        string baseUri,
        string relativeTarget,
        string expected)
    {
        HttpTargetComposer.Compose(baseUri, relativeTarget).ShouldBe(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Compose_AbsentOrEmptyPathReturnsTheBaseUriUnchanged(string? relativeTarget)
    {
        const string baseUri = "https://legacy.example.test/exact-target";

        string result = HttpTargetComposer.Compose(baseUri, relativeTarget);

        result.ShouldBe(baseUri);
    }

    [Theory]
    [InlineData("https://attacker.test/path")]
    [InlineData("//attacker.test/path")]
    [InlineData("path#fragment")]
    [InlineData("../outside")]
    [InlineData("inside/../../outside")]
    [InlineData("%2e%2e/outside")]
    [InlineData("%252e%252e/outside")]
    [InlineData("inside%2f..%2foutside")]
    public void Compose_RejectsTargetsThatEscapeOrAreNotRequestTargets(string relativeTarget)
    {
        Should.Throw<DeliveryConfigurationException>(
            () => HttpTargetComposer.Compose("https://destination.test/base", relativeTarget));
    }
}
