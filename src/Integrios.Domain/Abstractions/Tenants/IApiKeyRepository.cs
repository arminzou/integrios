using Integrios.Domain.Tenants;

namespace Integrios.Domain.Abstractions.Tenants;

public interface IApiKeyRepository
{
    Task<(ApiKey ApiKey, Tenant Tenant)?> FindActiveByPublicKeyAsync(string publicKey, CancellationToken cancellationToken = default);
}
