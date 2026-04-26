using Integrios.Domain.Tenants;

namespace Integrios.Application.Abstractions;

public interface IAdminKeyRepository
{
    Task<AdminKey?> FindActiveByPublicKeyAsync(string publicKey, CancellationToken cancellationToken = default);
}
