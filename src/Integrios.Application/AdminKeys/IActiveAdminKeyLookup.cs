using Integrios.Domain.Tenants;

namespace Integrios.Application.AdminKeys;

public interface IActiveAdminKeyLookup
{
    Task<AdminKey?> FindActiveByPublicKeyAsync(
        string publicKey,
        CancellationToken cancellationToken = default);
}
