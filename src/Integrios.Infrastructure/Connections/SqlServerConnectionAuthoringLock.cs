using System.Data.Common;
using Dapper;
using Integrios.Application.Connections;
using Integrios.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Integrios.Infrastructure.Connections;

internal sealed class SqlServerConnectionAuthoringLock(IDbContextFactory<IntegriosDbContext> contextFactory)
    : IConnectionAuthoringLock
{
    private const int LockTimeoutMilliseconds = 2_000;

    public async Task<IAsyncDisposable> AcquireAsync(
        IEnumerable<Guid> connectionIds,
        CancellationToken cancellationToken)
    {
        string[] resources = connectionIds
            .Distinct()
            .Order()
            .Select(id => $"connection:{id:N}")
            .ToArray();
        IntegriosDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);
        try
        {
            await context.Database.OpenConnectionAsync(cancellationToken);
            DbConnection connection = context.Database.GetDbConnection();
            var acquired = new List<string>(resources.Length);
            try
            {
                foreach (string resource in resources)
                {
                    int result = await ExecuteLockAsync(connection, resource, acquire: true, cancellationToken);
                    if (result < 0)
                        throw new ConnectionAuthoringConflictException();
                    acquired.Add(resource);
                }
                return new Lease(context, connection, acquired);
            }
            catch (Exception exception)
            {
                try
                {
                    await ReleaseAsync(connection, acquired);
                }
                catch
                {
                    // Preserve the acquisition failure; disposing the context closes the pinned session.
                }
                if (cancellationToken.IsCancellationRequested)
                    throw new OperationCanceledException("SQL Server application-lock acquisition was canceled.", exception, cancellationToken);
                throw;
            }
        }
        catch
        {
            try
            {
                await context.DisposeAsync();
            }
            catch
            {
                // Preserve the acquisition failure.
            }
            throw;
        }
    }

    private static Task<int> ExecuteLockAsync(
        DbConnection connection,
        string resource,
        bool acquire,
        CancellationToken cancellationToken) =>
        connection.ExecuteScalarAsync<int>(new CommandDefinition(
            acquire
                ? """
                  DECLARE @result int;
                  EXEC @result = sp_getapplock @Resource=@Resource, @LockMode='Exclusive',
                      @LockOwner='Session', @LockTimeout=@Timeout;
                  SELECT @result;
                  """
                : """
                  DECLARE @result int;
                  EXEC @result = sp_releaseapplock @Resource=@Resource, @LockOwner='Session';
                  SELECT @result;
                  """,
            new { Resource = resource, Timeout = LockTimeoutMilliseconds },
            cancellationToken: cancellationToken));

    private static async Task ReleaseAsync(DbConnection connection, IReadOnlyList<string> resources)
    {
        for (int index = resources.Count - 1; index >= 0; index--)
            await ExecuteLockAsync(connection, resources[index], acquire: false, CancellationToken.None);
    }

    private sealed class Lease(
        IntegriosDbContext context,
        DbConnection connection,
        IReadOnlyList<string> resources) : IAsyncDisposable
    {
        private bool disposed;

        public async ValueTask DisposeAsync()
        {
            if (disposed)
                return;
            disposed = true;
            try
            {
                await ReleaseAsync(connection, resources);
            }
            finally
            {
                await context.DisposeAsync();
            }
        }
    }
}
