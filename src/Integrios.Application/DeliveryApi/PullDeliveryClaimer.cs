using Integrios.Application.Delivery;
using Integrios.Application.Transforms;

namespace Integrios.Application.DeliveryApi;

// shortcut: a plain class while no host owns the DeliveryApi group; it becomes a handler that the
// delivery-api host registers once that host exists.
public sealed class PullDeliveryClaimer(IPullDeliveryQueue queue, ITransformEvaluator transformEvaluator)
{
    public async Task<IReadOnlyList<PullDelivery>> ClaimAsync(
        Guid destinationId,
        int max,
        CancellationToken cancellationToken)
    {
        var claimed = new List<PullDelivery>();
        while (claimed.Count < max)
        {
            PullDeliveryWorkItem? item = await queue.ClaimNextAsync(destinationId, cancellationToken);
            if (item is null)
                break;

            (string? payload, string? error) = DeliveryMapping.Apply(
                transformEvaluator,
                item.MappingConfigSnapshot,
                item.PayloadJson,
                new TransformContext(item.EventType, item.TopicName, item.AcceptedAt));
            if (error is not null)
            {
                await queue.SettleAsync(
                    new PullAttemptOwner(destinationId, item.Id, item.AttemptId),
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
