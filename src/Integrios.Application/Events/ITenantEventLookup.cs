namespace Integrios.Application.Events;

public interface ITenantEventLookup
{
    Task<GetEventResponse?> GetByIdAsync(
        Guid tenantId,
        Guid eventId,
        CancellationToken cancellationToken = default);
}
