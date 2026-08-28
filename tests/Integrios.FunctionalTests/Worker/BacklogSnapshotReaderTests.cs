using Integrios.Application.Delivery;
using Integrios.Infrastructure.Telemetry;

namespace Integrios.FunctionalTests.Worker;

public sealed class BacklogSnapshotReaderTests : IClassFixture<WorkerRoutingFixture>, IAsyncLifetime
{
    private readonly WorkerRoutingFixture fixture;

    public BacklogSnapshotReaderTests(WorkerRoutingFixture fixture) => this.fixture = fixture;

    public async Task InitializeAsync() => await fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task ReadAsync_UsesCreationForUnscheduledOutboxAndFirstAttemptDelivery()
    {
        Guid eventId = await fixture.InsertEventAndOutboxAsync("payment.created");
        await fixture.AgeOutboxAsync(eventId);

        BacklogSnapshot pending = await fixture.BacklogSnapshotReader.ReadAsync(CancellationToken.None);
        pending.PendingOutboxDepth.ShouldBe(1);
        pending.OldestPendingOutboxAgeSeconds.ShouldBeGreaterThan(0);
        pending.ReadyDeliveryDepth.ShouldBe(0);
        pending.OldestReadyDeliveryAgeSeconds.ShouldBe(0);

        (await fixture.RunFanoutBatchAsync()).ShouldBe(1);
        await fixture.AgeDeliveryAsync(eventId);

        BacklogSnapshot delivery = await fixture.BacklogSnapshotReader.ReadAsync(CancellationToken.None);
        delivery.PendingOutboxDepth.ShouldBe(0);
        delivery.OldestPendingOutboxAgeSeconds.ShouldBe(0);
        delivery.ReadyDeliveryDepth.ShouldBe(1);
        delivery.OldestReadyDeliveryAgeSeconds.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task ReadAsync_ExcludesFutureDeliveryRetryFromReadyDepthAndAge()
    {
        Guid eventId = await fixture.InsertEventAndOutboxAsync("payment.created");
        (await fixture.RunFanoutBatchAsync()).ShouldBe(1);
        await fixture.DeferDeliveryAsync(eventId);

        BacklogSnapshot snapshot = await fixture.BacklogSnapshotReader.ReadAsync(CancellationToken.None);

        snapshot.ReadyDeliveryDepth.ShouldBe(0);
        snapshot.OldestReadyDeliveryAgeSeconds.ShouldBe(0);
    }

    [Fact]
    public async Task ReadAsync_IncludesExpiredLeaseAndAnchorsAgeAtLeaseExpiry()
    {
        Guid deliveryId = await fixture.FanoutSingleDeliveryAsync();
        EventDeliveryWorkItem claimed = (await fixture.DeliveryQueue.ClaimNextAsync(CancellationToken.None))
            .ShouldBeOfType<EventDeliveryWorkItem>();
        claimed.Id.ShouldBe(deliveryId);
        await fixture.ForceLeaseExpiredAsync(deliveryId);

        BacklogSnapshot snapshot = await fixture.BacklogSnapshotReader.ReadAsync(CancellationToken.None);

        snapshot.ReadyDeliveryDepth.ShouldBe(1);
        snapshot.OldestReadyDeliveryAgeSeconds.ShouldBeGreaterThan(0);
    }
}
