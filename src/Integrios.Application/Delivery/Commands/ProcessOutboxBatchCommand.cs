using Integrios.Application.Telemetry;
using Integrios.Domain.Entities;
using Integrios.Domain.Enums;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Integrios.Application.Delivery;

public sealed record ProcessOutboxBatchCommand(int BatchSize) : IRequest<int>;

internal sealed class ProcessOutboxBatchCommandHandler(
    IOutboxFanout outboxFanout,
    IntegriosMetrics metrics,
    ILogger<ProcessOutboxBatchCommandHandler> logger) : IRequestHandler<ProcessOutboxBatchCommand, int>
{
    public async Task<int> Handle(ProcessOutboxBatchCommand command, CancellationToken cancellationToken)
    {
        var processedCount = 0;

        while (processedCount < command.BatchSize)
        {
            var result = await outboxFanout.ProcessNextAsync(cancellationToken);
            if (result is null)
                break;

            processedCount++;
            var scopeValues = new Dictionary<string, object> { ["event_id"] = result.EventId };
            if (result.TopicId is { } topicId)
                scopeValues["topic_id"] = topicId;

            using var scope = logger.BeginScope(scopeValues);

            if (result.EventStatus == EventStatus.Unrouted)
            {
                metrics.RecordEventUnrouted();
                logger.LogInformation(
                    "Event {EventId} has no matching Subscription. Marked unrouted.",
                    result.EventId);
                continue;
            }

            metrics.RecordFanoutRowsCreated(result.InsertedCount);
            logger.LogInformation(
                "Fanned out Event {EventId} to {MatchedCount} Subscription(s) ({InsertedCount} new EventDelivery rows).",
                result.EventId,
                result.MatchedCount,
                result.InsertedCount);
        }

        System.Diagnostics.Activity.Current?.SetTag("integrios.claimed_rows", processedCount);
        return processedCount;
    }
}
