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

        var topicId = await subscriptionRepository.FindTopicIdAsync(ev.TenantId, ev.EventType, cancellationToken);
        if (topicId is null)
        {
            logger.LogWarning("No active topic for tenant {TenantId} / event type {EventType}. Skipping event {EventId}.",
                ev.TenantId, ev.EventType, ev.Id);
            await outboxRepository.MarkProcessedAsync(row.Id, cancellationToken);
            return;
        }

        var subscriptions = await subscriptionRepository.GetActiveSubscriptionsAsync(topicId.Value, cancellationToken);
        var matching = subscriptions
            .Where(s => s.MatchEventTypes.Contains(ev.EventType, StringComparer.OrdinalIgnoreCase))
            .ToList();

        if (matching.Count == 0)
        {
            logger.LogInformation("Event {EventId} matched topic {TopicId} but no subscriptions. Marking completed.", ev.Id, topicId.Value);
            await outboxRepository.UpdateEventStatusAsync(ev.Id, "completed", topicId.Value, cancellationToken);
            await outboxRepository.MarkProcessedAsync(row.Id, cancellationToken);
            return;
        }

        var targets = matching
            .Select(s => new SubscriptionFanoutTarget(s.Id, s.DestinationConnectionId))
            .ToList();

        var inserted = await subscriptionDeliveryRepository.FanoutAsync(ev.Id, targets, cancellationToken);

        await outboxRepository.UpdateEventStatusAsync(ev.Id, "fanned_out", topicId.Value, cancellationToken);
        await outboxRepository.MarkProcessedAsync(row.Id, cancellationToken);

        logger.LogInformation(
            "Fanned out event {EventId} to {MatchedCount} subscription(s) ({InsertedCount} new delivery rows).",
            ev.Id, matching.Count, inserted);
    }
}
