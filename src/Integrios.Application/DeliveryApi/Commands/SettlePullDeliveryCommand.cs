using MediatR;

namespace Integrios.Application.DeliveryApi;

public sealed record SettlePullDeliveryCommand(PullAttemptOwner Owner, PullSettlement Settlement)
    : IRequest<PullSettlementStatus>;

internal sealed class SettlePullDeliveryCommandHandler(IPullDeliveryQueue queue)
    : IRequestHandler<SettlePullDeliveryCommand, PullSettlementStatus>
{
    public Task<PullSettlementStatus> Handle(SettlePullDeliveryCommand command, CancellationToken cancellationToken) =>
        queue.SettleAsync(command.Owner, command.Settlement, cancellationToken);
}
