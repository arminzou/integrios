namespace Integrios.Application.Auth;

public interface IPrincipalContext
{
    Guid? TenantId { get; }
    bool IsGlobal { get; }
}
