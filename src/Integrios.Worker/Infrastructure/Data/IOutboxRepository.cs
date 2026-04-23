namespace Integrios.Worker.Infrastructure.Data;

public interface IOutboxRepository
{
    Task<IReadOnlyList<OutboxRow>> ClaimBatchAsync(int limit, CancellationToken cancellationToken = default);
    Task MarkProcessedAsync(Guid outboxId, CancellationToken cancellationToken = default);
    Task<EventDetails?> GetEventAsync(Guid eventId, CancellationToken cancellationToken = default);
    Task UpdateEventStatusAsync(Guid eventId, string status, Guid? pipelineId, CancellationToken cancellationToken = default);
}

public record OutboxRow(Guid Id, Guid EventId);

public record EventDetails(Guid Id, Guid TenantId, string EventType, string PayloadJson);
