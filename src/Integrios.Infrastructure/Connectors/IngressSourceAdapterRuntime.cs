using Integrios.Application.Connectors;

namespace Integrios.Infrastructure.Connectors;

// Ingress alone binds the shared source-adapter catalog to runtime implementations, and fails
// startup rather than accept a manifest-selectable contract with nothing to execute it, or two
// implementations racing to own the same identity.
internal sealed class IngressSourceAdapterRuntime : IIngressSourceAdapterRuntime
{
    private readonly Dictionary<(string Key, int ContractVersion), IIngressSourceAdapter> adaptersByIdentity;

    public IngressSourceAdapterRuntime(IEnumerable<IIngressSourceAdapter> adapters, ISourceAdapterRegistry catalog)
    {
        adaptersByIdentity = adapters.ToDictionary(adapter => (adapter.Key, adapter.ContractVersion));

        foreach (SourceAdapterRegistration registration in catalog.GetAll())
        {
            if (!adaptersByIdentity.ContainsKey((registration.Key, registration.ContractVersion)))
            {
                throw new InvalidOperationException(
                    $"No Ingress runtime binding is registered for source adapter "
                    + $"'{registration.Key}' v{registration.ContractVersion}.");
            }
        }
    }

    public IIngressSourceAdapter GetRequired(string key, int contractVersion)
    {
        if (adaptersByIdentity.TryGetValue((key, contractVersion), out IIngressSourceAdapter? adapter))
            return adapter;

        throw new InvalidOperationException(
            $"No Ingress runtime binding is registered for source adapter '{key}' v{contractVersion}.");
    }
}
