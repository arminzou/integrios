using System.Data;
using Integrios.Application.AdminKeys;
using Integrios.Domain.Entities;
using Integrios.Domain.ValueObjects;
using Integrios.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Integrios.Infrastructure.AdminKeys;

internal sealed class AdminKeyRepository(IntegriosDbContext context)
    : IAdminKeyLookup, IAdminKeyLifecycle
{
    public Task<AdminKey?> FindActiveByPublicKeyAsync(
        string publicKey,
        CancellationToken cancellationToken) =>
        context.AdminKeys.AsNoTracking().SingleOrDefaultAsync(
            adminKey => adminKey.PublicKey == publicKey && adminKey.RevokedAt == null,
            cancellationToken);

    public Task<bool> HasLiveKeyAsync(CancellationToken cancellationToken) =>
        context.AdminKeys.AnyAsync(adminKey => adminKey.RevokedAt == null, cancellationToken);

    public async Task<AdminKey> InsertAsync(AdminKey adminKey, CancellationToken cancellationToken)
    {
        context.AdminKeys.Add(adminKey);
        await context.SaveChangesAsync(cancellationToken);
        return adminKey;
    }

    public async Task<AdminKey> RotateAsync(AdminKey newKey, CancellationToken cancellationToken)
    {
        await using var transaction = await context.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);

        int revoked = await context.AdminKeys
            .Where(adminKey => adminKey.RevokedAt == null)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(adminKey => adminKey.RevokedAt, DateTimeOffset.UtcNow),
                cancellationToken);
        if (revoked == 0)
        {
            throw new InvalidOperationException("No live AdminKey exists. Run bootstrap before rotation.");
        }

        context.AdminKeys.Add(newKey);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return newKey;
    }
}
