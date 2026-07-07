namespace Integrios.Application.Abstractions.Auth;

public interface ISecretResolver
{
    Task<string> ResolveAsync(Guid tenantId, string secretName, CancellationToken cancellationToken = default);
}
