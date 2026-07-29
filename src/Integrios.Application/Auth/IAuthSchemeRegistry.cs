namespace Integrios.Application.Auth;

public interface IAuthSchemeRegistry
{
    IAuthSchemeHandler GetRequired(string scheme);
    bool TryGet(string scheme, out IAuthSchemeHandler handler);
}
