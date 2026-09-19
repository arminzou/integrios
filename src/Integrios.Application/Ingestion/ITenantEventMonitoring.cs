namespace Integrios.Application.Ingestion;

// Separate from ITenantEventHistory: monitoring answers tenant-wide counts the Operator reads at a
// glance, never a page of Events, so it cannot turn the cursor-paginated ledger into a total-count API.
public interface ITenantEventMonitoring
{
    /// Current backlogs, however old. Never windowed: any window short of "all" hides the oldest
    /// waiting work first, which is exactly the work a stuck Worker or a missing Subscription leaves.
    Task<EventBacklogDto> GetBacklogAsync(Guid tenantId, CancellationToken cancellationToken);
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
