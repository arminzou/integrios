using Integrios.Application.Delivery;
using Integrios.Application.Telemetry;
using Integrios.Application.Transforms;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Integrios.Application.DeliveryApi;

public sealed record ClaimPullDeliveriesCommand(Guid DestinationId, int Max) : IRequest<IReadOnlyList<PullDelivery>>;

internal sealed class ClaimPullDeliveriesCommandHandler(
    IPullDeliveryQueue queue,
    ITransformEvaluator transformEvaluator,
    IntegriosMetrics metrics,
    ILogger<ClaimPullDeliveriesCommandHandler> logger)
    : IRequestHandler<ClaimPullDeliveriesCommand, IReadOnlyList<PullDelivery>>
{
    public async Task<IReadOnlyList<PullDelivery>> Handle(
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
