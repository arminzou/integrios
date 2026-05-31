using System.Diagnostics;
using Integrios.Application.Abstractions;
using Integrios.Application.Telemetry;
using Integrios.Domain.Events;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Integrios.Application.Outbox;

public sealed record ProcessOutboxBatchCommand(int BatchSize) : IRequest<int>;

internal sealed class ProcessOutboxBatchCommandHandler(
    IEventBus eventBus,
    ISubscriptionRepository subscriptionRepository,
    ISubscriptionDeliveryQueue subscriptionDeliveryQueue,
    IntegriosMetrics metrics,
    ILogger<ProcessOutboxBatchCommandHandler> logger) : IRequestHandler<ProcessOutboxBatchCommand, int>
{
    public async Task<int> Handle(ProcessOutboxBatchCommand command, CancellationToken cancellationToken)
    {
        var rows = await eventBus.ClaimBatchAsync(command.BatchSize, cancellationToken);

        // The ambient request span is the batch tick's own operational span, not a child of
        // any single event's trace.
        Activity.Current?.SetTag("claimed_rows", rows.Count);

        foreach (var row in rows)
            await FanoutRowAsync(row, cancellationToken);

        return rows.Count;
    }

    private async Task FanoutRowAsync(EventBusMessage row, CancellationToken cancellationToken)
    {
        // Re-parent under the originating event's trace via the stored traceparent.
        using var activity = ActivitySources.StartLinkedSpan("outbox.fanout", row.Traceparent);
        activity?.SetTag("event_id", row.EventId);

        var ev = await eventBus.GetEventAsync(row.EventId, cancellationToken);
        if (ev is null)
        {
            logger.LogWarning("Outbox row {OutboxId} references missing event {EventId}. Marking processed.", row.Id, row.EventId);
            await eventBus.MarkProcessedAsync(row.Id, cancellationToken);
            return;
        }

        if (ev.TopicId is null)
        {
            logger.LogInformation("Event {EventId} has no topic. Marking processed without fanout.", ev.Id);
            await eventBus.MarkProcessedAsync(row.Id, cancellationToken);
            return;
        }

        activity?.SetTag("topic_id", ev.TopicId);

        using var scope = logger.BeginScope(new Dictionary<string, object>
        {
            ["event_id"] = ev.Id,
            ["topic_id"] = ev.TopicId.Value
        });

        var subscriptions = await subscriptionRepository.GetActiveSubscriptionsAsync(ev.TopicId.Value, cancellationToken);
        var matching = subscriptions
            .Where(s => s.MatchEventTypes.Contains(ev.EventType, StringComparer.OrdinalIgnoreCase))
            .ToList();

        if (matching.Count == 0)
        {
            logger.LogInformation("Event {EventId} matched topic {TopicId} but no subscriptions. Marking unrouted.", ev.Id, ev.TopicId.Value);
            metrics.RecordEventUnrouted();
            await eventBus.UpdateEventStatusAsync(ev.Id, EventStatus.Unrouted, ev.TopicId, cancellationToken);
            await eventBus.MarkProcessedAsync(row.Id, cancellationToken);
            return;
        }

        var targets = matching
            .Select(s => new SubscriptionFanoutTarget(s.Id, s.DestinationConnectionId, s.TransformConfigJson))
            .ToList();

        // The fanout span's id anchors each delivery row, so dispatch and its retries stay on this trace.
        var inserted = await subscriptionDeliveryQueue.FanoutAsync(ev.Id, targets, activity?.Id, cancellationToken);
        metrics.RecordFanoutRowsCreated(inserted);

        await eventBus.UpdateEventStatusAsync(ev.Id, EventStatus.FannedOut, ev.TopicId, cancellationToken);
        await eventBus.MarkProcessedAsync(row.Id, cancellationToken);

        logger.LogInformation(
            "Fanned out event {EventId} to {MatchedCount} subscription(s) ({InsertedCount} new delivery rows).",
            ev.Id, matching.Count, inserted);
    }
}
