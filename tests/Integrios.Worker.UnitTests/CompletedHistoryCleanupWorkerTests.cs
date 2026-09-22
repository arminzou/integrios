using Integrios.Application.Delivery;
using Integrios.Tests.Shared;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Integrios.Worker.UnitTests;

public sealed class CompletedHistoryCleanupWorkerTests
{
    [Fact]
    public async Task SuccessfulSweep_UsesFixedBounds_AndLogsOneCompletionRecord()
    {
        using var cancellation = new CancellationTokenSource();
        var cleanup = Substitute.For<ICompletedHistoryCleanup>();
        DateTimeOffset cutoff = DateTimeOffset.UtcNow.AddDays(-7);
        cleanup.SweepAsync(TimeSpan.FromDays(7), 500, Arg.Any<CancellationToken>())
            .Returns(new CompletedHistoryCleanupResult(true, cutoff, 42, 3));
        var loggerProvider = new CapturingLoggerProvider();
        using ILoggerFactory loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(loggerProvider));
        var delay = new CancellingDelay(cancellation);
        var worker = new CompletedHistoryCleanupWorker(
            cleanup,
            new HistoryRetentionOptions(TimeSpan.FromDays(7)),
            loggerFactory.CreateLogger<CompletedHistoryCleanupWorker>(),
            delay);

        await worker.RunAsync(cancellation.Token);

        await cleanup.Received(1).SweepAsync(
            TimeSpan.FromDays(7), HistoryRetentionOptions.BatchSize, Arg.Any<CancellationToken>());
        delay.Delays.ShouldBe([HistoryRetentionOptions.SweepInterval]);
        CapturedLogRecord record = loggerProvider.Records.ShouldHaveSingleItem();
        record.Level.ShouldBe(LogLevel.Information);
        record.Message.ShouldContain("42 Events in 3 batches");
        var fields = ((IEnumerable<KeyValuePair<string, object?>>)record.State)
            .ToDictionary(pair => pair.Key, pair => pair.Value);
        fields["Cutoff"].ShouldBe(cutoff);
        fields["DeletedEventCount"].ShouldBe(42);
        fields["BatchCount"].ShouldBe(3);
    }

    [Fact]
    public async Task FailedSweep_LogsOneSanitizedRecord_AndWaitsForTheNextCadence()
    {
        using var cancellation = new CancellationTokenSource();
        const string sensitive = "tenant payload secret Server=private";
        var loggerProvider = new CapturingLoggerProvider();
        using ILoggerFactory loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(loggerProvider));
        var delay = new CancellingDelay(cancellation);
        var worker = new CompletedHistoryCleanupWorker(
            _ => Task.FromException<CompletedHistoryCleanupResult>(new InvalidOperationException(sensitive)),
            loggerFactory.CreateLogger<CompletedHistoryCleanupWorker>(),
            delay);

        await worker.RunAsync(cancellation.Token);

        delay.Delays.ShouldBe([HistoryRetentionOptions.SweepInterval]);
        CapturedLogRecord record = loggerProvider.Records.ShouldHaveSingleItem();
        record.Level.ShouldBe(LogLevel.Error);
        record.Exception.ShouldBeNull();
        record.Message.ShouldContain(nameof(InvalidOperationException));
        record.Message.ShouldNotContain(sensitive);
    }

    [Fact]
    public async Task Cancellation_StopsWithoutFailureRecordOrDelay()
    {
        using var cancellation = new CancellationTokenSource();
        var loggerProvider = new CapturingLoggerProvider();
        using ILoggerFactory loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(loggerProvider));
        var delay = new RecordingDelay();
        var worker = new CompletedHistoryCleanupWorker(
            async token =>
            {
                await cancellation.CancelAsync();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return new CompletedHistoryCleanupResult(true, DateTimeOffset.UtcNow, 0, 0);
            },
            loggerFactory.CreateLogger<CompletedHistoryCleanupWorker>(),
            delay);

        await worker.RunAsync(cancellation.Token);

        loggerProvider.Records.ShouldBeEmpty();
        delay.Delays.ShouldBeEmpty();
    }

    [Fact]
    public async Task CleanupFailure_DoesNotStopFanoutOrDelivery()
    {
        using var cancellation = new CancellationTokenSource();
        var fanoutRan = NewSignal();
        var deliveryRan = NewSignal();
        var delay = new BlockingDelay();
        using ILoggerFactory loggerFactory = LoggerFactory.Create(_ => { });
        var cleanup = new CompletedHistoryCleanupWorker(
            _ => Task.FromException<CompletedHistoryCleanupResult>(new InvalidOperationException("cleanup")),
            loggerFactory.CreateLogger<CompletedHistoryCleanupWorker>(),
            delay);
        var fanout = new OutboxFanoutWorker(
            token => SignalOnceThenWaitAsync(fanoutRan, token),
            loggerFactory.CreateLogger<OutboxFanoutWorker>(),
            new FanoutLoopOptions(10, TimeSpan.FromSeconds(2)),
            delay);
        var delivery = new EventDeliveryWorker(
            token => SignalOnceThenWaitAsync(deliveryRan, token),
            loggerFactory.CreateLogger<EventDeliveryWorker>(),
            new DeliveryLoopOptions(25, TimeSpan.FromSeconds(2)),
            delay);

        Task cleanupTask = cleanup.RunAsync(cancellation.Token);
        Task fanoutTask = fanout.RunAsync(cancellation.Token);
        Task deliveryTask = delivery.RunAsync(cancellation.Token);
        await fanoutRan.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await deliveryRan.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await cancellation.CancelAsync();
        await Task.WhenAll(cleanupTask, fanoutTask, deliveryTask).WaitAsync(TimeSpan.FromSeconds(2));
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task<int> SignalOnceThenWaitAsync(
        TaskCompletionSource signal,
        CancellationToken cancellationToken)
    {
        if (signal.TrySetResult())
            return 1;

        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        return 0;
    }

    private sealed class CancellingDelay(CancellationTokenSource cancellation) : IWorkerLoopDelay
    {
        public List<TimeSpan> Delays { get; } = [];

        public async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            Delays.Add(delay);
            await cancellation.CancelAsync();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private sealed class RecordingDelay : IWorkerLoopDelay
    {
        public List<TimeSpan> Delays { get; } = [];

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            Delays.Add(delay);
            return Task.CompletedTask;
        }
    }

    private sealed class BlockingDelay : IWorkerLoopDelay
    {
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
            Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }
}
