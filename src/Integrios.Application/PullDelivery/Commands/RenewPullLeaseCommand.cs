using MediatR;

namespace Integrios.Application.PullDelivery;

public sealed record RenewPullLeaseCommand(PullAttemptOwner Owner) : IRequest<PullLeaseRenewalResult>;

internal sealed class RenewPullLeaseCommandHandler(IPullDeliveryQueue queue)
    : IRequestHandler<RenewPullLeaseCommand, PullLeaseRenewalResult>
{
    public Task<PullLeaseRenewalResult> Handle(RenewPullLeaseCommand command, CancellationToken cancellationToken) =>
        queue.RenewAsync(command.Owner, cancellationToken);
}
