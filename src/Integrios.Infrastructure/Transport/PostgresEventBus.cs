using Integrios.Application.Abstractions;

namespace Integrios.Infrastructure.Transport;

public sealed class PostgresEventBus(IOutboxRepository outboxRepository) : IEventBus
{
    public async Task<IReadOnlyList<EventBusMessage>> ClaimBatchAsync(int limit, CancellationToken cancellationToken = default)
    {
        var rows = await outboxRepository.ClaimBatchAsync(limit, cancellationToken);
        return rows.Select(row => new EventBusMessage(row.Id, row.EventId, row.AttemptCount)).ToList();
    }

    public Task<EventDetails?> GetEventAsync(Guid eventId, CancellationToken cancellationToken = default)
        => outboxRepository.GetEventAsync(eventId, cancellationToken);

    public Task MarkProcessedAsync(Guid messageId, CancellationToken cancellationToken = default)
        => outboxRepository.MarkProcessedAsync(messageId, cancellationToken);

    public Task UpdateEventStatusAsync(Guid eventId, string status, Guid? topicId, CancellationToken cancellationToken = default)
        => outboxRepository.UpdateEventStatusAsync(eventId, status, topicId, cancellationToken);
}
