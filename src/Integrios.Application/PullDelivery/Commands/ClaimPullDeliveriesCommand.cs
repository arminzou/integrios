using System.Diagnostics;
using Integrios.Application.Delivery;
using Integrios.Application.Telemetry;
using Integrios.Application.Transforms;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Integrios.Application.PullDelivery;

// Waits up to Wait for work, so an idle Subscriber holds one request open instead of polling.
public sealed record ClaimPullDeliveriesCommand(Guid DestinationId, int Max, TimeSpan Wait)
    : IRequest<IReadOnlyList<PullDelivery>>;

internal sealed class ClaimPullDeliveriesCommandHandler(
    IPullDeliveryQueue queue,
    IDestinationPollRecorder pollRecorder,
    ITransformEvaluator transformEvaluator,
    IntegriosMetrics metrics,
    ILogger<ClaimPullDeliveriesCommandHandler> logger)
    : IRequestHandler<ClaimPullDeliveriesCommand, IReadOnlyList<PullDelivery>>
{
    // shortcut: a fixed one-second re-check bounds idle claim latency at about a second; move to
    // database notifications if measurement shows that is not good enough.
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    public async Task<IReadOnlyList<PullDelivery>> Handle(
        ClaimPullDeliveriesCommand command,
        CancellationToken cancellationToken)
    {
        await pollRecorder.RecordPollAsync(command.DestinationId, DateTimeOffset.UtcNow, cancellationToken);
        long started = Stopwatch.GetTimestamp();
        while (true)
        {
            IReadOnlyList<PullDelivery> claimed = await ClaimAvailableAsync(command, cancellationToken);
            TimeSpan remaining = command.Wait - Stopwatch.GetElapsedTime(started);
            if (claimed.Count > 0 || remaining <= TimeSpan.Zero)
                return claimed;
            await Task.Delay(remaining < PollInterval ? remaining : PollInterval, cancellationToken);
        }
    }

    private async Task<IReadOnlyList<PullDelivery>> ClaimAvailableAsync(
        ClaimPullDeliveriesCommand command,
        CancellationToken cancellationToken)
    {
        var claimed = new List<PullDelivery>();
        try
        {
            while (claimed.Count < command.Max)
            {
                EventDeliveryClaimResult? claim = await queue.ClaimNextAsync(command.DestinationId, cancellationToken);
                if (claim is null)
                    break;
                if (claim is RecoveredEventDeliveryDeadLetter recovered)
                {
                    RecoveredDeadLetterSignal.Emit(logger, metrics, recovered);
                    continue;
                }
                if (claim is not ClaimedPullDelivery { WorkItem: var item })
                    throw new InvalidOperationException($"Unknown pull claim result '{claim.GetType().Name}'.");

                (string? payload, string? error) = DeliveryMapping.Apply(
                    transformEvaluator,
                    item.MappingConfigSnapshot,
                    item.PayloadJson,
                    new TransformContext(item.EventType, item.TopicName, item.AcceptedAt));
                if (error is not null)
                {
                    await queue.SettleAsync(
                        new PullAttemptOwner(command.DestinationId, item.Id, item.AttemptId),
                        PullSettlement.MappingFailed(error),
                        cancellationToken);
                    continue;
                }

                claimed.Add(new PullDelivery(
                    item.Id,
                    item.AttemptId,
                    item.AttemptNumber,
                    item.EventId,
                    item.EventType,
                    item.TopicName,
                    item.AcceptedAt,
                    payload!,
                    item.LeaseExpiresAt,
                    item.Traceparent));
            }
        }
        catch (Exception exception) when (claimed.Count > 0)
        {
            // These deliveries are already leased. Handing them out lets the Subscriber settle them;
            // dropping them would leave each to expire and spend its attempt as indeterminate.
            logger.LogWarning(
                exception,
                "Stopped claiming for Destination {DestinationId} after {ClaimedCount} deliveries; returning those.",
                command.DestinationId,
                claimed.Count);
        }

        return claimed;
    }
}

public sealed record PullDelivery(
    Guid DeliveryId,
    Guid AttemptId,
    int AttemptNumber,
    Guid EventId,
    string EventType,
    string? TopicName,
    DateTimeOffset AcceptedAt,
    string PayloadJson,
    DateTimeOffset LeaseExpiresAt,
    string? Traceparent);
