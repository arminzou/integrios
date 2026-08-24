using System.Data;
using Integrios.Application.Authoring.OperatorKeys;
using Integrios.Domain.Entities;
using Integrios.Domain.ValueObjects;
using Integrios.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Integrios.Infrastructure.OperatorKeys;

internal sealed class OperatorKeyRepository(IntegriosDbContext context)
    : IOperatorKeyLookup, IOperatorKeyLifecycle
{
    public Task<OperatorKey?> FindActiveByPublicKeyAsync(
        string publicKey,
        CancellationToken cancellationToken) =>
        context.OperatorKeys.AsNoTracking().SingleOrDefaultAsync(
            operatorKey => operatorKey.PublicKey == publicKey && operatorKey.RevokedAt == null,
            cancellationToken);

    public Task<bool> HasLiveKeyAsync(CancellationToken cancellationToken) =>
        context.OperatorKeys.AnyAsync(operatorKey => operatorKey.RevokedAt == null, cancellationToken);

    public async Task<OperatorKey> InsertAsync(OperatorKey operatorKey, CancellationToken cancellationToken)
    {
        context.OperatorKeys.Add(operatorKey);
        await context.SaveChangesAsync(cancellationToken);
        return operatorKey;
    }

    public async Task<OperatorKey> RotateAsync(OperatorKey newKey, CancellationToken cancellationToken)
    {
        await using var transaction = await context.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);

        int revoked = await context.OperatorKeys
            .Where(operatorKey => operatorKey.RevokedAt == null)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(operatorKey => operatorKey.RevokedAt, DateTimeOffset.UtcNow),
                cancellationToken);
        if (revoked == 0)
        {
            throw new InvalidOperationException("No live OperatorKey exists. Run bootstrap before rotation.");
        }

        context.OperatorKeys.Add(newKey);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return newKey;
    }
}
