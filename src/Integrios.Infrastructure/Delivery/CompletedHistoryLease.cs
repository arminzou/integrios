namespace Integrios.Infrastructure.Delivery;

internal static class CompletedHistoryLease
{
    internal static async Task ReleaseAsync(
        Func<Task> release,
        Action discardConnectionPool,
        Exception? sweepFailure)
    {
        try
        {
            await release();
        }
        catch
        {
            try
            {
                discardConnectionPool();
            }
            catch
            {
                // The original sweep or release failure remains authoritative.
            }

            if (sweepFailure is null)
                throw;
        }
    }
}
