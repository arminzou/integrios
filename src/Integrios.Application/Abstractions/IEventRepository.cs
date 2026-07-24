using Integrios.Application.Events;

namespace Integrios.Application.Abstractions;

public interface IEventRepository
{
    Task<IngestEventResponse> IngestAsync(Guid tenantId, IngestEventRequest request, Guid? topicId, string? traceparent = null, CancellationToken cancellationToken = default);
    Task<GetEventResponse?> GetEventByIdAsync(Guid tenantId, Guid eventId, CancellationToken cancellationToken = default);
}
