using Integrios.Application.Delivery;
using MediatR;

namespace Integrios.Application.Recovery;

public sealed record ReplaySubscriptionDeliveryCommand(
    Guid TenantId,
    Guid EventId,
    Guid SubscriptionDeliveryId)
    : IRequest<DeadLetterReplayResult>;

internal sealed class ReplaySubscriptionDeliveryCommandHandler(IDeadLetterReplay deadLetterReplay)
    : IRequestHandler<ReplaySubscriptionDeliveryCommand, DeadLetterReplayResult>
{
    public Task<DeadLetterReplayResult> Handle(
        ReplaySubscriptionDeliveryCommand command,
        CancellationToken cancellationToken) =>
        deadLetterReplay.ReplayAsync(
            command.TenantId,
            command.EventId,
            command.SubscriptionDeliveryId,
            cancellationToken);
}
