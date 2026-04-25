using Integrios.Domain.Tenants;

namespace Integrios.Application.Abstractions;

public interface IApiKeyRepository
{
    Task<(ApiKey ApiKey, Tenant Tenant)?> FindActiveByPublicKeyAsync(string publicKey, CancellationToken cancellationToken = default);
}
