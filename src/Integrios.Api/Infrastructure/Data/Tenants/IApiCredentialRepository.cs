using Integrios.Core.Domain.Tenants;

namespace Integrios.Api.Infrastructure.Data.Tenants;

public interface IApiCredentialRepository
{
    Task<(ApiCredential Credential, Tenant Tenant)?> FindActiveByKeyIdAsync(string keyId, CancellationToken cancellationToken = default);
}
