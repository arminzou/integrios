using Integrios.Application.Delivery;
using MediatR;

namespace Integrios.Application.Recovery;

public sealed record ReplayEventDeliveryCommand(
    Guid TenantId,
    Guid EventId,
    Guid EventDeliveryId)
    : IRequest<DeadLetterReplayResult>;

internal sealed class ReplayEventDeliveryCommandHandler(IDeadLetterReplay deadLetterReplay)
    : IRequestHandler<ReplayEventDeliveryCommand, DeadLetterReplayResult>
{
    public Task<DeadLetterReplayResult> Handle(
        ReplayEventDeliveryCommand command,
        CancellationToken cancellationToken) =>
        deadLetterReplay.ReplayAsync(
            command.TenantId,
            command.EventId,
            command.EventDeliveryId,
            cancellationToken);
}
