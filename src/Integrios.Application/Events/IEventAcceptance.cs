namespace Integrios.Application.Events;

public interface IEventAcceptance
{
    Task<IngestEventResponse> AcceptAsync(
        Guid tenantId,
        IngestEventRequest request,
        Guid topicId,
        string? traceparent = null,
        CancellationToken cancellationToken = default);
}
