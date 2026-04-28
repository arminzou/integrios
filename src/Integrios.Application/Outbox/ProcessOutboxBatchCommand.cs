using Integrios.Application.Abstractions;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Integrios.Application.Outbox;

public sealed record ProcessOutboxBatchCommand(int BatchSize) : IRequest<int>;

internal sealed class ProcessOutboxBatchCommandHandler(
    IOutboxRepository outboxRepository,
    ISubscriptionRepository subscriptionRepository,
    ISubscriptionDeliveryRepository subscriptionDeliveryRepository,
    ILogger<ProcessOutboxBatchCommandHandler> logger) : IRequestHandler<ProcessOutboxBatchCommand, int>
{
    public async Task<int> Handle(ProcessOutboxBatchCommand command, CancellationToken cancellationToken)
    {
        var rows = await outboxRepository.ClaimBatchAsync(command.BatchSize, cancellationToken);

        foreach (var row in rows)
            await FanoutRowAsync(row, cancellationToken);

        return rows.Count;
    }

    private async Task FanoutRowAsync(OutboxRow row, CancellationToken cancellationToken)
    {
        var ev = await outboxRepository.GetEventAsync(row.EventId, cancellationToken);
        if (ev is null)
        {
            logger.LogWarning("Outbox row {OutboxId} references missing event {EventId}. Marking processed.", row.Id, row.EventId);
            await outboxRepository.MarkProcessedAsync(row.Id, cancellationToken);
            return;
        }

        if (ev.TopicId is null)
        {
            logger.LogInformation("Event {EventId} has no topic. Marking processed without fanout.", ev.Id);
            await outboxRepository.MarkProcessedAsync(row.Id, cancellationToken);
            return;
        }

        var subscriptions = await subscriptionRepository.GetActiveSubscriptionsAsync(ev.TopicId.Value, cancellationToken);
        var matching = subscriptions
            .Where(s => s.MatchEventTypes.Contains(ev.EventType, StringComparer.OrdinalIgnoreCase))
            .ToList();

        if (matching.Count == 0)
        {
            logger.LogInformation("Event {EventId} matched topic {TopicId} but no subscriptions. Marking completed.", ev.Id, ev.TopicId.Value);
            await outboxRepository.UpdateEventStatusAsync(ev.Id, "completed", ev.TopicId, cancellationToken);
            await outboxRepository.MarkProcessedAsync(row.Id, cancellationToken);
            return;
        }

        var targets = matching
            .Select(s => new SubscriptionFanoutTarget(s.Id, s.DestinationConnectionId))
            .ToList();

        var inserted = await subscriptionDeliveryRepository.FanoutAsync(ev.Id, targets, cancellationToken);

        await outboxRepository.UpdateEventStatusAsync(ev.Id, "fanned_out", ev.TopicId, cancellationToken);
        await outboxRepository.MarkProcessedAsync(row.Id, cancellationToken);

        logger.LogInformation(
            "Fanned out event {EventId} to {MatchedCount} subscription(s) ({InsertedCount} new delivery rows).",
            ev.Id, matching.Count, inserted);
    }
}
