using System.Buffers.Binary;
using System.Data.Common;
using System.Diagnostics;
using System.Security.Cryptography;
using Dapper;
using Integrios.Application.Authoring.Destinations;
using Integrios.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Integrios.Infrastructure.Destinations;

internal sealed class PostgresDestinationAuthoringLock(IDbContextFactory<IntegriosDbContext> contextFactory)
    : IDestinationAuthoringLock
{
    private static readonly TimeSpan AcquisitionBudget = TimeSpan.FromSeconds(2);

    public async Task<IAsyncDisposable> AcquireAsync(
        IEnumerable<Guid> destinationIds,
        CancellationToken cancellationToken)
    {
        long[] keys = destinationIds
            .Distinct()
            .Select(ToAdvisoryLockKey)
            .Order()
            .ToArray();
        IntegriosDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);
        try
        {
            await context.Database.OpenConnectionAsync(cancellationToken);
        }
        catch
        {
            await context.DisposeAsync();
            throw;
        }
        DbConnection connection = context.Database.GetDbConnection();
        var acquiredKeys = new List<long>(keys.Length);

        var elapsed = Stopwatch.StartNew();
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                bool acquiredAll = true;
                foreach (long key in keys)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    bool acquired = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
                        "SELECT pg_try_advisory_lock(@Key)",
                        new { Key = key },
                        cancellationToken: cancellationToken));
                    if (!acquired)
                    {
                        acquiredAll = false;
                        break;
                    }

                    acquiredKeys.Add(key);
                }

                if (acquiredAll)
                    return new DestinationAuthoringLease(context, connection, acquiredKeys);

                await UnlockAsync(connection, acquiredKeys);
                acquiredKeys.Clear();
                TimeSpan remaining = AcquisitionBudget - elapsed.Elapsed;
                if (remaining <= TimeSpan.Zero)
                    throw new DestinationAuthoringConflictException();

                TimeSpan delay = TimeSpan.FromMilliseconds(Random.Shared.Next(20, 76));
                if (delay > remaining)
                    delay = remaining;
                await Task.Delay(delay, cancellationToken);
            }
        }
        catch
        {
            await ReleaseAfterFailedAcquisitionAsync(context, connection, acquiredKeys);
            throw;
        }
    }

    private static long ToAdvisoryLockKey(Guid id)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(id.ToByteArray(), hash);
        return BinaryPrimitives.ReadInt64BigEndian(hash);
    }

    private static async Task ReleaseAfterFailedAcquisitionAsync(
        IntegriosDbContext context,
        DbConnection connection,
        IReadOnlyList<long> acquiredKeys)
    {
        try
        {
            await UnlockAsync(connection, acquiredKeys);
        }
        catch
        {
            // Preserve the acquisition failure. Disposing a broken connection
            // prevents its session from returning to the pool with locks held.
        }

        try
        {
            await context.DisposeAsync();
        }
        catch
        {
            // Preserve the acquisition failure.
        }
    }

    private static async Task UnlockAsync(DbConnection connection, IReadOnlyList<long> keys)
    {
        for (var index = keys.Count - 1; index >= 0; index--)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "SELECT pg_advisory_unlock(@Key)",
                new { Key = keys[index] }));
        }
    }

    private sealed class DestinationAuthoringLease(
        IntegriosDbContext context,
        DbConnection connection,
        IReadOnlyList<long> keys) : IAsyncDisposable
    {
        private bool disposed;

        public async ValueTask DisposeAsync()
        {
            if (disposed)
                return;

            disposed = true;
            try
            {
                await UnlockAsync(connection, keys);
            }
            finally
            {
                await context.DisposeAsync();
            }
        }
    }
}
