namespace Integrios.Application.Abstractions;

public interface IEventBus
{
    Task<IReadOnlyList<EventBusMessage>> ClaimBatchAsync(int limit, CancellationToken cancellationToken = default);
    Task<EventDetails?> GetEventAsync(Guid eventId, CancellationToken cancellationToken = default);
    Task MarkProcessedAsync(Guid messageId, CancellationToken cancellationToken = default);
    Task UpdateEventStatusAsync(Guid eventId, string status, Guid? topicId, CancellationToken cancellationToken = default);
}

public sealed record EventBusMessage(Guid Id, Guid EventId, int AttemptCount);
