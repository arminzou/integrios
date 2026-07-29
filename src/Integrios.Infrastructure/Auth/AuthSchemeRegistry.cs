using Integrios.Application.Auth;
using Integrios.Application.Delivery;

namespace Integrios.Infrastructure.Auth;

public sealed class AuthSchemeRegistry(IEnumerable<IAuthSchemeHandler> handlers) : IAuthSchemeRegistry
{
    private readonly Dictionary<string, IAuthSchemeHandler> handlersByName =
        handlers.ToDictionary(handler => handler.Name, StringComparer.OrdinalIgnoreCase);

    public IAuthSchemeHandler GetRequired(string scheme)
    {
        if (TryGet(scheme, out IAuthSchemeHandler handler))
        {
            return handler;
        }

        throw new DeliveryConfigurationException($"Unknown auth scheme '{scheme}'.");
    }

    public bool TryGet(string scheme, out IAuthSchemeHandler handler)
    {
        return handlersByName.TryGetValue(scheme, out handler!);
    }
}
