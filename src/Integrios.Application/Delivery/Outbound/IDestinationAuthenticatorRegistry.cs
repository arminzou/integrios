namespace Integrios.Application.Delivery;

public interface IDestinationAuthenticatorRegistry
{
    IDestinationAuthenticator GetRequired(string scheme);
    bool TryGet(string scheme, out IDestinationAuthenticator handler);
}
