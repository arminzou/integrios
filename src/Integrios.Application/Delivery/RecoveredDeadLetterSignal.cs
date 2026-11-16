using Integrios.Application.Telemetry;
using Microsoft.Extensions.Logging;

namespace Integrios.Application.Delivery;

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
