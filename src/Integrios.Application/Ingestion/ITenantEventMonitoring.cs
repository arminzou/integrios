namespace Integrios.Application.Ingestion;

// Separate from ITenantEventHistory: monitoring answers tenant-wide counts the Operator reads at a
// glance, never a page of Events, so it cannot turn the cursor-paginated ledger into a total-count API.
public interface ITenantEventMonitoring
{
    /// Current backlogs, however old. Never windowed: any window short of "all" hides the oldest
    /// waiting work first, which is exactly the work a stuck Worker or a missing Subscription leaves.
    Task<EventBacklogDto> GetBacklogAsync(Guid tenantId, CancellationToken cancellationToken);

    /// Events accepted in [windowStart, windowStart + bucketCount * bucketLength), counted per bucket
    /// by current outcome. Only buckets holding at least one Event are returned; the caller zero-fills.
    /// windowStart must fall on a whole second so bucket boundaries are exact on every provider.
    Task<IReadOnlyList<EventActivityBucketCounts>> GetActivityAsync(
        Guid tenantId,
        DateTimeOffset windowStart,
        TimeSpan bucketLength,
        int bucketCount,
        CancellationToken cancellationToken);
}

/// Unrouted counts only actionable Events: a live Topic with at least one non-deleted Source on it
/// that still declares the Event's exact type. A historical-only unrouted Event stays in Event
/// history but no longer reads as work current configuration could route.
public sealed record EventBacklogDto(
    EventBacklogItemDto AwaitingRouting,
    EventBacklogItemDto Unrouted,
    EventBacklogItemDto DeadLetteredDeliveries);

/// OldestAt is when the oldest item entered the backlog: the Event's acceptance for the Event
/// backlogs, and the Delivery's failure for dead-lettered Deliveries. Null only when Count is zero.
public sealed record EventBacklogItemDto(int Count, DateTimeOffset? OldestAt);

/// Each Event counts exactly once, by its current outcome: accepted is awaiting routing, unrouted is
/// unrouted, any other Event with a currently dead-lettered EventDelivery is delivery dead-lettered,
/// and the rest are routed. Several dead-lettered Deliveries on one Event still count one Event.
public sealed record EventActivityBucketCounts(
    int BucketIndex, int AwaitingRouting, int Unrouted, int DeliveryDeadLettered, int Routed);
