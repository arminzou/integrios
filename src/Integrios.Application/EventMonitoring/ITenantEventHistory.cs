using Integrios.Domain.Enums;

namespace Integrios.Application.EventMonitoring;

// Separate from ITenantEventLookup: Ingestion resolves one Event by id, while only the Operator
// control plane browses Tenant Event history behind a protected cursor.
public interface ITenantEventHistory
{
    /// Watermark is issued only for a first page: the newest (accepted_at, id) the filter matched as
    /// the page was read, protected and scoped to the Tenant and filter like a cursor.
    Task<(IReadOnlyList<EventListItemDto> Items, string? NextCursor, string? Watermark)> ListAsync(
        Guid tenantId,
        TenantEventFilter filter,
        string? afterCursor,
        int limit,
        CancellationToken cancellationToken);

    /// Events matching the same filter accepted after the watermark, counted up to limit so an
    /// unattended screen on a busy Tenant stays cheap. Throws InvalidCursorException for a watermark
    /// that is expired, forged, or was issued for another Tenant or filter.
    Task<int> CountNewerAsync(
        Guid tenantId,
        TenantEventFilter filter,
        string watermark,
        int limit,
        CancellationToken cancellationToken);
}

/// Bounded Event-history filters. DeliveryStatus matches an Event with at least one EventDelivery in
/// that state; it never changes the Event's own status.
public sealed record TenantEventFilter(
    EventStatus? Status,
    string? DeliveryStatus,
    Guid? SourceId,
    Guid? TopicId,
    string? SourceEventId,
    DateTimeOffset? AcceptedFrom,
    DateTimeOffset? AcceptedTo,
    string? EventType = null);
