using System.Text.Json;
using Integrios.Application.Secrets;

namespace Integrios.Application.UnitTests.Secrets;

public sealed class SecretReferenceMapTests
{
    [Theory]
    [InlineData("a")]
    [InlineData("api-key")]
    [InlineData("github-webhook-secret")]
    [InlineData("0-9")]
    public void AcceptsTenantSlugGrammar(string reference)
    {
        SecretReferenceMap.Validate(Refs(reference), "secret_refs").ShouldBeNull();
    }

    [Theory]
    [InlineData("api_key")]
    [InlineData("API-KEY")]
    [InlineData("-api-key")]
    [InlineData("api-key-")]
    [InlineData("api key")]
    [InlineData("")]
    [InlineData("api-key\n")]
    [InlineData("api--key")]
    public void RejectsAnythingOutsideTenantSlugGrammar(string reference)
    {
        SecretReferenceMap.Validate(Refs(reference), "secret_refs").ShouldNotBeNull();
    }

    [Fact]
    public void RejectsAReferenceLongerThanADnsLabel()
    {
        SecretReferenceMap.Validate(Refs(new string('a', 63)), "secret_refs").ShouldBeNull();
        SecretReferenceMap.Validate(Refs(new string('a', 64)), "secret_refs").ShouldNotBeNull();
    }

    private static JsonElement Refs(string reference) =>
        JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["api_key"] = reference });
}
