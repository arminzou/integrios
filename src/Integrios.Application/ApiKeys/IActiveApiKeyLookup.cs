using Integrios.Domain.Tenants;

namespace Integrios.Application.ApiKeys;

public interface IActiveApiKeyLookup
{
    Task<(ApiKey ApiKey, Tenant Tenant)?> FindActiveByKeyHashAsync(
        string keyHash,
        CancellationToken cancellationToken);
}
