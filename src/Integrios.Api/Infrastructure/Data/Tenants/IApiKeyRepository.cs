using Integrios.Core.Domain.Tenants;

namespace Integrios.Api.Infrastructure.Data.Tenants;

public interface IApiKeyRepository
{
    Task<(ApiKey ApiKey, Tenant Tenant)?> FindActiveByPublicKeyAsync(
        string publicKey,
        CancellationToken cancellationToken = default);
}
