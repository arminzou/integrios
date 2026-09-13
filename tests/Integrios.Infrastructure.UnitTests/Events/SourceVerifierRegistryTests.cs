using Integrios.Application.Ingestion;
using Integrios.Infrastructure.Events;

namespace Integrios.Infrastructure.UnitTests;

public sealed class SourceVerifierRegistryTests
{
    [Fact]
    public void Registry_EnumeratesRegisteredVerifiers()
    {
        ISourceVerifierRegistry registry = new SourceVerifierRegistry([new HmacSha256SourceVerifier()]);

        ISourceVerifier verifier = registry.Registered.ShouldHaveSingleItem();
        verifier.Scheme.ShouldBe("hmac_sha256");
        verifier.RequiredConfigFields.ShouldBeEmpty();
        verifier.RequiredSecretFields.ShouldBe(["secret"]);
    }
}
