using Integrios.Domain.Enums;

namespace Integrios.Application.Ingestion;

// Separate from ITenantEventLookup: Ingestion resolves one Event by id, while only the Operator
// control plane browses Tenant Event history behind a protected cursor.
public interface ITenantEventHistory
{
    Task<(IReadOnlyList<EventListItemDto> Items, string? NextCursor)> ListAsync(
        Guid tenantId,
        TenantEventFilter filter,
        string? afterCursor,
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
    DateTimeOffset? AcceptedTo);
