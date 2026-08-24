using Integrios.Application.Delivery;

namespace Integrios.Infrastructure.Delivery;

internal sealed class DestinationAuthenticatorRegistry(IEnumerable<IDestinationAuthenticator> handlers) : IDestinationAuthenticatorRegistry
{
    private readonly Dictionary<string, IDestinationAuthenticator> handlersByName =
        handlers.ToDictionary(handler => handler.Name, StringComparer.OrdinalIgnoreCase);

    public IDestinationAuthenticator GetRequired(string scheme)
    {
        if (TryGet(scheme, out IDestinationAuthenticator handler))
        {
            return handler;
        }

        throw new DeliveryConfigurationException($"Unknown auth scheme '{scheme}'.");
    }

    public bool TryGet(string scheme, out IDestinationAuthenticator handler)
    {
        return handlersByName.TryGetValue(scheme, out handler!);
    }
}
