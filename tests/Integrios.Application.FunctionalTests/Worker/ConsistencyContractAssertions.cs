namespace Integrios.Application.FunctionalTests.Worker;

internal static class ConsistencyContractAssertions
{
    internal static async Task FanoutProcessesOnceAsync(
        Guid eventId,
        IReadOnlyCollection<int> processedCounts,
        Func<Guid, Task<int>> getDeliveryCount,
        Func<Guid, Task<bool>> isOutboxProcessed,
        Func<Guid, Task<string?>> getEventStatus)
    {
        Assert.Equal(1, processedCounts.Sum());
        Assert.Equal(1, await getDeliveryCount(eventId));
        Assert.True(await isOutboxProcessed(eventId));
        Assert.Equal("routed", await getEventStatus(eventId));
    }

    internal static async Task ClaimFailureRollsBackAsync(
        Guid deliveryId,
        Func<Guid, Task<EventDeliveryState>> getDelivery,
        Func<Guid, Task<IReadOnlyList<DeliveryAttemptState>>> getAttempts)
    {
        EventDeliveryState delivery = await getDelivery(deliveryId);
        Assert.Equal("pending", delivery.Status);
        Assert.Equal(0, delivery.LifetimeAttemptCount);
        Assert.Equal(0, delivery.RetryCycleAttemptCount);
        Assert.Null(delivery.ActiveAttemptId);
        Assert.Empty(await getAttempts(deliveryId));
    }

    internal static async Task FinalizationFailureRollsBackAsync(
        Guid deliveryId,
        Guid attemptId,
        Func<Guid, Task<EventDeliveryState>> getDelivery,
        Func<Guid, Task<IReadOnlyList<DeliveryAttemptState>>> getAttempts)
    {
        EventDeliveryState delivery = await getDelivery(deliveryId);
        Assert.Equal("in_flight", delivery.Status);
        Assert.Equal(attemptId, delivery.ActiveAttemptId);
        Assert.NotNull(delivery.LeaseExpiresAt);

        DeliveryAttemptState attempt = Assert.Single(await getAttempts(deliveryId));
        Assert.Equal("in_progress", attempt.Status);
        Assert.Null(attempt.CompletedAt);
    }
}
