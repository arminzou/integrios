using Integrios.Application.Telemetry;
using Microsoft.Extensions.Logging;

namespace Integrios.Application.Delivery;

// Push dispatch and the pull claim both reclaim expired leases, so both report a recovery that
// dead-lettered its delivery the same way.
internal static class RecoveredDeadLetterSignal
{
    public static void Emit(ILogger logger, IntegriosMetrics metrics, RecoveredEventDeliveryDeadLetter recovered)
    {
        metrics.RecordDeliveryDeadLettered(recovered.ConnectorKey);
        logger.LogWarning(
            "Recovered expired attempt_id={AttemptId}, attempt_number={AttemptNumber} and dead-lettered delivery_id={DeliveryId}, subscription_id={SubscriptionId}, event_id={EventId}",
            recovered.AttemptId,
            recovered.AttemptNumber,
            recovered.DeliveryId,
            recovered.SubscriptionId,
            recovered.EventId);
    }
}
