namespace Integrios.Application.Delivery;

public interface ICompletedHistoryCleanup
{
    Task<CompletedHistoryCleanupResult> SweepAsync(
        TimeSpan retentionPeriod,
        int batchSize,
        CancellationToken cancellationToken);
}

public sealed record CompletedHistoryCleanupResult(
    bool Acquired,
    DateTimeOffset? Cutoff,
    int DeletedEventCount,
    int BatchCount);
