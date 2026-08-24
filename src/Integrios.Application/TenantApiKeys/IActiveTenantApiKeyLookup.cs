using Integrios.Domain.Entities;
using Integrios.Domain.ValueObjects;

namespace Integrios.Application.TenantApiKeys;

public interface IActiveTenantApiKeyLookup
{
    Task<(TenantApiKey TenantApiKey, Tenant Tenant)?> FindActiveByKeyHashAsync(
        string keyHash,
        CancellationToken cancellationToken);
}
