using Integrios.Application.Abstractions;

namespace Integrios.Infrastructure.Transport;

public sealed class PostgresSubscriptionDeliveryQueue(ISubscriptionDeliveryRepository repository) : ISubscriptionDeliveryQueue
{
    public Task<int> FanoutAsync(Guid eventId, IReadOnlyList<SubscriptionFanoutTarget> targets, CancellationToken cancellationToken = default)
        => repository.FanoutAsync(eventId, targets, cancellationToken);

    public Task<IReadOnlyList<SubscriptionDeliveryWorkItem>> ClaimBatchAsync(int limit, CancellationToken cancellationToken = default)
        => repository.ClaimBatchAsync(limit, cancellationToken);

    public Task MarkSucceededAsync(Guid deliveryId, CancellationToken cancellationToken = default)
        => repository.MarkSucceededAsync(deliveryId, cancellationToken);

    public Task ScheduleRetryAsync(Guid deliveryId, int newAttemptCount, DateTimeOffset deliverAfter, CancellationToken cancellationToken = default)
        => repository.ScheduleRetryAsync(deliveryId, newAttemptCount, deliverAfter, cancellationToken);

    public Task MarkDeadLetteredAsync(Guid deliveryId, CancellationToken cancellationToken = default)
        => repository.MarkDeadLetteredAsync(deliveryId, cancellationToken);
}
