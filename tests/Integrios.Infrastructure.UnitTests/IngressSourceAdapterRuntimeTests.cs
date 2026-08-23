using System.Text.Json;
using Integrios.Application.Events;
using Integrios.Application.Connectors;
using Integrios.Infrastructure.Connectors;

namespace Integrios.Infrastructure.UnitTests;

// Ingress binds the shared source-adapter catalog to runtime implementations; these prove the
// startup-time failure modes i7a.5 requires (missing and duplicate bindings), independent of DI.
public class IngressSourceAdapterRuntimeTests
{
    [Fact]
    public void Constructor_EveryCatalogRegistrationHasABinding_Succeeds()
    {
        var runtime = new IngressSourceAdapterRuntime(
            [new FakeAdapter("verified_webhook", 1)],
            new FakeCatalog(new SourceAdapterRegistration("verified_webhook", 1, true, false, ["hmac_sha256"], _ => { })));

        Assert.Same(runtime.GetRequired("verified_webhook", 1).GetType(), typeof(FakeAdapter));
    }

    [Fact]
    public void Constructor_CatalogRegistrationWithoutBinding_Throws()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => new IngressSourceAdapterRuntime(
            [],
            new FakeCatalog(new SourceAdapterRegistration("verified_webhook", 1, true, false, ["hmac_sha256"], _ => { }))));

        Assert.Contains("verified_webhook", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_DuplicateAdapterBinding_Throws()
    {
        Assert.ThrowsAny<ArgumentException>(() => new IngressSourceAdapterRuntime(
            [new FakeAdapter("verified_webhook", 1), new FakeAdapter("verified_webhook", 1)],
            new FakeCatalog()));
    }

    [Fact]
    public void GetRequired_UnknownIdentity_Throws()
    {
        var runtime = new IngressSourceAdapterRuntime([], new FakeCatalog());

        Assert.Throws<InvalidOperationException>(() => runtime.GetRequired("unknown", 1));
    }

    private sealed class FakeAdapter(string key, int contractVersion) : IIngressSourceAdapter
    {
        public string Key => key;
        public int ContractVersion => contractVersion;

        public Task<EventSubmission> ExecuteAsync(
            SourceAdapterExecutionContext context, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FakeCatalog(params SourceAdapterRegistration[] registrations) : ISourceAdapterRegistry
    {
        public bool TryGet(string key, int contractVersion, out SourceAdapterRegistration registration)
        {
            registration = registrations.FirstOrDefault(r => r.Key == key && r.ContractVersion == contractVersion)!;
            return registration is not null;
        }

        public IReadOnlyCollection<SourceAdapterRegistration> GetAll() => registrations;
    }
}
