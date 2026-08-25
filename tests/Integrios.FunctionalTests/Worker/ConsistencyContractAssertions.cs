namespace Integrios.FunctionalTests.Worker;

internal static class ConsistencyContractAssertions
{
    internal static async Task FanoutProcessesOnceAsync(
        Guid eventId,
        IReadOnlyCollection<int> processedCounts,
        Func<Guid, Task<int>> getDeliveryCount,
        Func<Guid, Task<bool>> isOutboxProcessed,
        Func<Guid, Task<string?>> getEventStatus)
    {
        processedCounts.Sum().ShouldBe(1);
        (await getDeliveryCount(eventId)).ShouldBe(1);
        (await isOutboxProcessed(eventId)).ShouldBeTrue();
        (await getEventStatus(eventId)).ShouldBe("routed");
    }

    internal static async Task ClaimFailureRollsBackAsync(
        Guid deliveryId,
        Func<Guid, Task<EventDeliveryState>> getDelivery,
        Func<Guid, Task<IReadOnlyList<DeliveryAttemptState>>> getAttempts)
    {
        EventDeliveryState delivery = await getDelivery(deliveryId);
        delivery.Status.ShouldBe("pending");
        delivery.LifetimeAttemptCount.ShouldBe(0);
        delivery.RetryCycleAttemptCount.ShouldBe(0);
        delivery.ActiveAttemptId.ShouldBeNull();
        (await getAttempts(deliveryId)).ShouldBeEmpty();
    }

    internal static async Task FinalizationFailureRollsBackAsync(
        Guid deliveryId,
        Guid attemptId,
        Func<Guid, Task<EventDeliveryState>> getDelivery,
        Func<Guid, Task<IReadOnlyList<DeliveryAttemptState>>> getAttempts)
    {
        EventDeliveryState delivery = await getDelivery(deliveryId);
        delivery.Status.ShouldBe("in_flight");
        delivery.ActiveAttemptId.ShouldBe(attemptId);
        delivery.LeaseExpiresAt.ShouldNotBeNull();

        DeliveryAttemptState attempt = (await getAttempts(deliveryId)).ShouldHaveSingleItem();
        attempt.Status.ShouldBe("in_progress");
        attempt.CompletedAt.ShouldBeNull();
    }
}
