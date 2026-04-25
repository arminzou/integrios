using Integrios.Domain.Contracts;

namespace Integrios.Domain.Abstractions.Events;

public interface IEventRepository
{
    Task<IngestEventResponse> IngestAsync(Guid tenantId, IngestEventRequest request, CancellationToken cancellationToken = default);
    Task<GetEventResponse?> GetEventByIdAsync(Guid tenantId, Guid eventId, CancellationToken cancellationToken = default);
    Task<bool> ReplayEventAsync(Guid tenantId, Guid eventId, CancellationToken cancellationToken = default);
}
