namespace Integrios.Application.Ingestion;

// Separate from ITenantEventHistory: this answers one bounded window snapshot rather than a
// cursor-paginated Event list, and it counts Events and EventDeliveries separately so fanout
// cannot duplicate an Event count.
public interface ITenantEventActivitySummary
{
    Task<EventActivitySummaryCounts> GetAsync(
        Guid tenantId,
        TenantEventActivityFilter filter,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        CancellationToken cancellationToken);
}

/// Only Source and Topic scope the summary; Event-status and EventDelivery-status filters do not,
/// so the four reported values remain comparable to one another.
public sealed record TenantEventActivityFilter(Guid? SourceId, Guid? TopicId);

public sealed record EventActivitySummaryCounts(
    int EventsAccepted, int AwaitingRouting, int Unrouted, int DeadLetteredDeliveries);
