namespace Integrios.Application.Delivery;

public interface IDestinationAuthenticatorRegistry
{
    IReadOnlyCollection<IDestinationAuthenticator> Registered { get; }
    IDestinationAuthenticator GetRequired(string scheme);
    bool TryGet(string scheme, out IDestinationAuthenticator handler);
}
