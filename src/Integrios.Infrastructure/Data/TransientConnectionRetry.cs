using System.Data.Common;

namespace Integrios.Infrastructure.Data;

// EF Core's retrying execution strategy only covers work that goes through a DbContext. The
// per-provider adapters open their own connections through IDbConnectionFactory, so connection
// establishment -- the fault the hosted databases actually produce -- is retried here instead.
// Only the open is retried: a fault mid-command belongs to the caller's transaction.
internal static class TransientConnectionRetry
{
    private const int MaxAttempts = 4;

    public static async ValueTask<DbConnection> OpenAsync(
        Func<CancellationToken, ValueTask<DbConnection>> open,
        CancellationToken cancellationToken)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return await open(cancellationToken);
            }
            catch (DbException exception) when (exception.IsTransient && attempt < MaxAttempts)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt), cancellationToken);
            }
        }
    }
}
