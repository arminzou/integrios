using Integrios.Domain.Entities;
using Integrios.Domain.ValueObjects;

namespace Integrios.Application.AdminKeys;

public interface IAdminKeyLookup
{
    Task<AdminKey?> FindActiveByPublicKeyAsync(
        string publicKey,
        CancellationToken cancellationToken);
}
