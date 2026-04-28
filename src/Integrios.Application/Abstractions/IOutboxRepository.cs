namespace Integrios.Application.Abstractions;

public interface IOutboxRepository
{
    Task<IReadOnlyList<OutboxRow>> ClaimBatchAsync(int limit, CancellationToken cancellationToken = default);
    Task MarkProcessedAsync(Guid outboxId, CancellationToken cancellationToken = default);
    Task ScheduleRetryAsync(Guid outboxId, int newAttemptCount, DateTimeOffset deliverAfter, CancellationToken cancellationToken = default);
    Task<EventDetails?> GetEventAsync(Guid eventId, CancellationToken cancellationToken = default);
    Task UpdateEventStatusAsync(Guid eventId, string status, Guid? topicId, CancellationToken cancellationToken = default);
}

public record OutboxRow(Guid Id, Guid EventId, int AttemptCount);

public record EventDetails(Guid Id, Guid TenantId, string EventType, string PayloadJson, Guid? TopicId);
