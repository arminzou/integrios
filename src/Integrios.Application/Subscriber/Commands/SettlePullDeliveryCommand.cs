using Integrios.Application.Delivery;
using Integrios.Application.Telemetry;
using Integrios.Domain.Entities;
using MediatR;

namespace Integrios.Application.Subscriber;

public sealed record SettlePullDeliveryCommand(PullAttemptOwner Owner, PullSettlement Settlement)
    : IRequest<PullSettlementStatus>;

internal sealed class SettlePullDeliveryCommandHandler(IPullDeliveryQueue queue, IntegriosMetrics metrics)
    : IRequestHandler<SettlePullDeliveryCommand, PullSettlementStatus>
{
    public async Task<PullSettlementStatus> Handle(SettlePullDeliveryCommand command, CancellationToken cancellationToken)
    {
        PullSettlementResult result = await queue.SettleAsync(command.Owner, command.Settlement, cancellationToken);
        if (result.Status != PullSettlementStatus.Applied)
            return result.Status;

        switch (result.Disposition)
        {
            case EventDeliveryDisposition.Succeeded:
                metrics.RecordDeliverySucceeded(Connector.PullConnectorKey);
                break;
            case EventDeliveryDisposition.RetryScheduled:
                metrics.RecordDeliveryFailed(Connector.PullConnectorKey, "error");
                break;
            case EventDeliveryDisposition.DeadLettered:
                metrics.RecordDeliveryDeadLettered(Connector.PullConnectorKey);
                break;
        }

        return result.Status;
    }
}
