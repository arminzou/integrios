using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Integrios.Subscriber.UnitTests;

public sealed class DeliveryLoopTests : IAsyncDisposable
{
    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(30);
    private readonly CountingTimeProvider time = new();
    private readonly FakeTransport transport;
    private DeliveryLoop<TestHandler>? loop;

    public DeliveryLoopTests()
    {
        transport = new FakeTransport(time);
    }

    public async ValueTask DisposeAsync()
    {
        if (loop is not null)
            await loop.StopAsync(CancellationToken.None);
    }

    private async Task StartAsync(Func<Delivery, CancellationToken, Task<DeliveryResult>> handle, int concurrency = 1)
    {
        ServiceProvider services = new ServiceCollection().AddSingleton(new TestHandler(handle)).BuildServiceProvider();
        loop = new DeliveryLoop<TestHandler>(
            transport, services.GetRequiredService<IServiceScopeFactory>(), concurrency, time,
            NullLogger<DeliveryLoop<TestHandler>>.Instance);
        await loop.StartAsync(CancellationToken.None);
    }

    private Delivery Enqueue()
    {
        var delivery = new Delivery(
            Guid.NewGuid(), Guid.NewGuid(), 1, Guid.NewGuid(), "order.placed", null, time.GetUtcNow(),
            JsonDocument.Parse("{}").RootElement, time.GetUtcNow() + Lease, null);
        transport.Queue.Writer.TryWrite(delivery);
        return delivery;
    }

    private static async Task Eventually(Func<bool> condition)
    {
        for (int i = 0; i < 500 && !condition(); i++)
            await Task.Delay(10);
        condition().ShouldBeTrue();
    }

    [Fact]
    public async Task ASuccessfulHandlerAcks()
    {
        await StartAsync((_, _) => Task.FromResult(DeliveryResult.Ack));
        Delivery delivery = Enqueue();

        await Eventually(() => transport.Settled.Count == 1);

        transport.Settled.ShouldHaveSingleItem().ShouldBe((delivery.AttemptId, Settlement.Ack));
    }

    [Fact]
    public async Task ARetryResultNacksWithItsDelayInWholeSeconds()
    {
        await StartAsync((_, _) => Task.FromResult(DeliveryResult.Retry(TimeSpan.FromSeconds(90.2), "busy")));
        Enqueue();

        await Eventually(() => transport.Settled.Count == 1);

        transport.Settled.Single().Settlement.ShouldBe(new Settlement("nack", 91, "busy"));
    }

    [Fact]
    public async Task AHandlerExceptionNacksWithThePlatformBackoff()
    {
        await StartAsync((_, _) => throw new InvalidOperationException("boom"));
        Enqueue();

        await Eventually(() => transport.Settled.Count == 1);

        transport.Settled.Single().Settlement.ShouldBe(new Settlement("nack", null, "boom"));
    }

    [Fact]
    public async Task APermanentFailureFails()
    {
        await StartAsync((_, _) => throw new PermanentDeliveryFailureException("order rejected"));
        Enqueue();

        await Eventually(() => transport.Settled.Count == 1);

        transport.Settled.Single().Settlement.ShouldBe(new Settlement("fail", null, "order rejected"));
    }

    [Fact]
    public async Task ErrorTextIsCutToWhatTheDeliveryApiAccepts()
    {
        await StartAsync((_, _) => throw new InvalidOperationException(new string('x', 5000)));
        Enqueue();

        await Eventually(() => transport.Settled.Count == 1);

        transport.Settled.Single().Settlement.Error!.Length.ShouldBe(2000);
    }

    [Fact]
    public void ARetryDelayOutsideWhatTheDeliveryApiAcceptsIsRefused()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => DeliveryResult.Retry(TimeSpan.FromSeconds(-1)));
        Should.Throw<ArgumentOutOfRangeException>(() => DeliveryResult.Retry(TimeSpan.FromSeconds(901)));
    }

    [Fact]
    public async Task TheLeaseRenewsAtAThirdOfItsDurationWhileTheHandlerRuns()
    {
        var release = new TaskCompletionSource<DeliveryResult>();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await StartAsync((_, _) =>
        {
            started.SetResult();
            return release.Task;
        });
        DateTimeOffset claimedAt = time.GetUtcNow();
        Enqueue();
        await started.Task;

        time.Advance(TimeSpan.FromSeconds(9));
        transport.Renewals.ShouldBeEmpty();
        time.Advance(TimeSpan.FromSeconds(1));
        await Eventually(() => transport.Renewals.Count == 1 && time.TimersCreated == 3);
        time.Advance(TimeSpan.FromSeconds(10));
        await Eventually(() => transport.Renewals.Count == 2);
        release.SetResult(DeliveryResult.Ack);
        await Eventually(() => transport.Settled.Count == 1);

        transport.Renewals.ShouldBe([claimedAt.AddSeconds(10), claimedAt.AddSeconds(20)]);
    }

    [Fact]
    public async Task LostOwnershipCancelsTheHandlerAndReportsNoOutcome()
    {
        transport.RenewAnswer = () => null;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource();
        await StartAsync(async (_, token) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            cancelled.SetResult();
            return DeliveryResult.Ack;
        });
        Enqueue();
        await started.Task;

        time.Advance(TimeSpan.FromSeconds(10));
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Eventually(() => transport.Claims == 2);

        transport.Renewals.ShouldHaveSingleItem();
        transport.Settled.ShouldBeEmpty();
    }

    [Fact]
    public async Task AnAttemptThatLostOwnershipAtSettlementIsNotReportedAgain()
    {
        transport.SettleAnswer = () => false;
        await StartAsync((_, _) => Task.FromResult(DeliveryResult.Ack));
        Enqueue();
        await Eventually(() => transport.Claims == 2);

        time.Advance(TimeSpan.FromMinutes(1));
        await Task.Delay(50);

        transport.Settled.Count.ShouldBe(1);
    }

    [Fact]
    public async Task ShutdownCancelsTheHandlerAndReportsItForRetry()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await StartAsync(async (_, token) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            return DeliveryResult.Ack;
        });
        Enqueue();
        await started.Task;

        await loop!.StopAsync(CancellationToken.None);

        transport.Settled.ShouldHaveSingleItem().Settlement.Action.ShouldBe("nack");
    }

    [Fact]
    public async Task ClaimFailuresBackOffExponentially()
    {
        transport.ClaimFailures = 3;
        await StartAsync((_, _) => Task.FromResult(DeliveryResult.Ack));
        await Eventually(() => time.TimersCreated == 1);

        time.Advance(TimeSpan.FromSeconds(1));
        await Eventually(() => time.TimersCreated == 2);
        transport.Claims.ShouldBe(2);
        time.Advance(TimeSpan.FromSeconds(1.9));
        transport.Claims.ShouldBe(2);
        time.Advance(TimeSpan.FromSeconds(0.1));
        await Eventually(() => time.TimersCreated == 3);
        transport.Claims.ShouldBe(3);
        time.Advance(TimeSpan.FromSeconds(4));
        await Eventually(() => transport.Claims == 4);
    }

    [Fact]
    public async Task SettlementRetriesATransportFailure()
    {
        int settleCalls = 0;
        transport.SettleAnswer = () => ++settleCalls == 1 ? throw new HttpRequestException("reset") : true;
        await StartAsync((_, _) => Task.FromResult(DeliveryResult.Ack));
        Enqueue();
        await Eventually(() => settleCalls == 1 && time.TimersCreated == 3);

        time.Advance(TimeSpan.FromSeconds(1));

        await Eventually(() => settleCalls == 2);
    }

    [Fact]
    public async Task EachClaimLoopHandlesADeliveryConcurrently()
    {
        int running = 0;
        var release = new TaskCompletionSource<DeliveryResult>();
        await StartAsync(
            (_, _) =>
            {
                Interlocked.Increment(ref running);
                return release.Task;
            },
            concurrency: 3);
        for (int i = 0; i < 4; i++)
            Enqueue();

        await Eventually(() => running == 3);
        await Task.Delay(50);
        running.ShouldBe(3);
        release.SetResult(DeliveryResult.Ack);
        await Eventually(() => transport.Settled.Count == 4);
    }

    [Fact]
    public async Task AStalledRenewalCancelsTheHandlerWhenItsLeaseExpires()
    {
        transport.RenewRequest = async token =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return null;
        };
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await StartAsync(async (_, token) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            cancelled.SetResult();
            return DeliveryResult.Ack;
        });
        Enqueue();
        await started.Task;

        time.Advance(TimeSpan.FromSeconds(10));
        await Eventually(() => transport.Renewals.Count == 1);
        time.Advance(TimeSpan.FromSeconds(20));

        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Eventually(() => transport.Claims == 2);
        transport.Settled.ShouldBeEmpty();
    }

    [Fact]
    public async Task RenewalFailuresStopAtLeaseExpiryWithoutAnotherRequest()
    {
        transport.RenewAnswer = () => throw new HttpRequestException("offline");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await StartAsync(async (_, token) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            cancelled.SetResult();
            return DeliveryResult.Ack;
        });
        Enqueue();
        await started.Task;

        time.Advance(TimeSpan.FromSeconds(10));
        await Eventually(() => transport.Renewals.Count == 1 && time.TimersCreated == 3);
        time.Advance(TimeSpan.FromSeconds(20));

        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        transport.Renewals.Count.ShouldBe(1);
        transport.Settled.ShouldBeEmpty();
    }

    [Fact]
    public async Task AStalledSettlementStopsAtLeaseExpiry()
    {
        var settling = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.SettleRequest = async token =>
        {
            settling.SetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, token);
            }
            finally
            {
                cancelled.SetResult();
            }
            return true;
        };
        await StartAsync((_, _) => Task.FromResult(DeliveryResult.Ack));
        Enqueue();
        await settling.Task.WaitAsync(TimeSpan.FromSeconds(5));

        time.Advance(Lease);

        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Eventually(() => transport.Claims == 2);
        await loop!.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
    }

    /// Counts timers, so a test advances time only once the loop is waiting on one.
    private sealed class CountingTimeProvider() : FakeTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"))
    {
        private int timersCreated;

        public int TimersCreated => Volatile.Read(ref timersCreated);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Interlocked.Increment(ref timersCreated);
            return base.CreateTimer(callback, state, dueTime, period);
        }
    }

    public sealed class TestHandler(Func<Delivery, CancellationToken, Task<DeliveryResult>> handle) : IDeliveryHandler
    {
        public Task<DeliveryResult> HandleAsync(Delivery delivery, CancellationToken cancellationToken) =>
            handle(delivery, cancellationToken);
    }

    private sealed class FakeTransport(TimeProvider time) : IDeliveryTransport
    {
        private int claims;

        public Channel<Delivery> Queue { get; } = Channel.CreateUnbounded<Delivery>();
        public ConcurrentQueue<DateTimeOffset> Renewals { get; } = new();
        public ConcurrentQueue<(Guid AttemptId, Settlement Settlement)> Settled { get; } = new();
        public int ClaimFailures { get; set; }
        public Func<DateTimeOffset?> RenewAnswer { get; set; } = () => time.GetUtcNow() + Lease;
        public Func<CancellationToken, Task<DateTimeOffset?>>? RenewRequest { get; set; }
        public Func<bool> SettleAnswer { get; set; } = () => true;
        public Func<CancellationToken, Task<bool>>? SettleRequest { get; set; }
        public int Claims => Volatile.Read(ref claims);

        public async Task<IReadOnlyList<Delivery>> ClaimAsync(int max, int waitSeconds, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref claims) <= ClaimFailures)
                throw new HttpRequestException("connection refused");
            return [await Queue.Reader.ReadAsync(cancellationToken)];
        }

        public Task<DateTimeOffset?> RenewAsync(Delivery delivery, CancellationToken cancellationToken)
        {
            Renewals.Enqueue(time.GetUtcNow());
            return RenewRequest is { } request ? request(cancellationToken) : Task.FromResult(RenewAnswer());
        }

        public Task<bool> SettleAsync(Delivery delivery, Settlement settlement, CancellationToken cancellationToken)
        {
            if (SettleRequest is { } request)
                return request(cancellationToken);
            bool answer = SettleAnswer();
            Settled.Enqueue((delivery.AttemptId, settlement));
            return Task.FromResult(answer);
        }
    }
}
