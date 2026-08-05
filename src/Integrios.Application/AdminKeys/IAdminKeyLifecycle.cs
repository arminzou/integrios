using Integrios.Domain.Tenants;

namespace Integrios.Application.AdminKeys;

public interface IAdminKeyLifecycle
{
    Task<bool> HasLiveKeyAsync(CancellationToken cancellationToken);

    Task<AdminKey> InsertAsync(
        AdminKey adminKey,
        CancellationToken cancellationToken);

    Task<AdminKey> RotateAsync(
        AdminKey newKey,
        CancellationToken cancellationToken);
}
