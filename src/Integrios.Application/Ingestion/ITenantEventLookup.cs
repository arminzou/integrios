namespace Integrios.Application.Ingestion;

public interface ITenantEventLookup
{
    Task<EventDto?> GetByIdAsync(
        Guid tenantId,
        Guid eventId,
        CancellationToken cancellationToken);
}
