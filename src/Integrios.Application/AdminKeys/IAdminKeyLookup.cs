using Integrios.Domain.Tenants;

namespace Integrios.Application.AdminKeys;

public interface IAdminKeyLookup
{
    Task<AdminKey?> FindActiveByPublicKeyAsync(
        string publicKey,
        CancellationToken cancellationToken);
}
