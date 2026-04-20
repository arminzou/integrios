using Integrios.Core.Contracts;

namespace Integrios.Api.Infrastructure.Data.Events;

public interface IEventRepository
{
    Task<IngestEventResponse> IngestAsync(
        Guid tenantId,
        IngestEventRequest request,
        CancellationToken cancellationToken = default);

    Task<GetEventResponse?> GetEventByIdAsync(
        Guid tenantId,
        Guid eventId,
        CancellationToken cancellationToken = default);
}
