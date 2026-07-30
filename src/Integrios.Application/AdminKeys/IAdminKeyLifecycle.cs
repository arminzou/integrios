using Integrios.Domain.Tenants;

namespace Integrios.Application.AdminKeys;

public interface IAdminKeyLifecycle
{
    Task<bool> HasLiveKeyAsync(CancellationToken cancellationToken = default);

    Task<AdminKey> InsertAsync(
        AdminKey adminKey,
        CancellationToken cancellationToken = default);

    Task<AdminKey> RotateAsync(
        AdminKey newKey,
        CancellationToken cancellationToken = default);
}
