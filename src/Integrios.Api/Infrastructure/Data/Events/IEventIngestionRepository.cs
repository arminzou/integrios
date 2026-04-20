using Integrios.Core.Contracts;

namespace Integrios.Api.Infrastructure.Data.Events;

public interface IEventIngestionRepository
{
    Task<IngestEventResponse> IngestAsync(
        Guid tenantId,
        IngestEventRequest request,
        CancellationToken cancellationToken = default);
}
