namespace Integrios.Application.Events;

public interface ITenantEventLookup
{
    Task<EventDto?> GetByIdAsync(
        Guid tenantId,
        Guid eventId,
        CancellationToken cancellationToken);
}
