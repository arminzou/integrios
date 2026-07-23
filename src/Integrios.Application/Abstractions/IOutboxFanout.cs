using Integrios.Domain.Events;

namespace Integrios.Application.Abstractions;

public interface IOutboxFanout
{
    Task<OutboxFanoutResult?> ProcessNextAsync(CancellationToken cancellationToken = default);
}

public sealed record OutboxFanoutResult(
    Guid EventId,
    Guid? TopicId,
    EventStatus EventStatus,
    int MatchedCount,
    int InsertedCount);
