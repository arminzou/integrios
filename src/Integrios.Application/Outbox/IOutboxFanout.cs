using Integrios.Domain.Entities;
using Integrios.Domain.Enums;

namespace Integrios.Application.Outbox;

public interface IOutboxFanout
{
    Task<OutboxFanoutResult?> ProcessNextAsync(CancellationToken cancellationToken);
}

public sealed record OutboxFanoutResult(
    Guid EventId,
    Guid? TopicId,
    EventStatus EventStatus,
    int MatchedCount,
    int InsertedCount);
