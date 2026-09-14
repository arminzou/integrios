using System.Data.Common;
using Microsoft.EntityFrameworkCore;

namespace Integrios.Infrastructure.Data;

// EF Core's retrying execution strategy only covers work that goes through a DbContext. The
// per-provider adapters open their own connections through IDbConnectionFactory, so the open
// borrows that same strategy -- and with it the provider's own transient-fault classification.
// Hand-written classification is what went wrong here before: DbException.IsTransient reads false
// for every SqlException because Microsoft.Data.SqlClient does not override it, so a check built
// on it retried Npgsql and silently did nothing for SQL Server.
//
// Only the open is retried: a fault mid-command belongs to the caller's transaction.
internal static class TransientConnectionRetry
{
    public static async ValueTask<DbConnection> OpenAsync(
        IDbContextFactory<IntegriosDbContext> contextFactory,
        Func<CancellationToken, Task<DbConnection>> open,
        CancellationToken cancellationToken)
    {
        await using IntegriosDbContext context =
            await contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.Database.CreateExecutionStrategy().ExecuteAsync(open, cancellationToken);
    }
}
