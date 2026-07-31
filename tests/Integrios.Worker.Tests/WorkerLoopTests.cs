using Integrios.Application.Delivery;
using Integrios.Application.Outbox;
using Integrios.Application.Transforms;
using MediatR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Integrios.Worker.Tests;

public sealed class WorkerLoopTests
{
    [Fact]
    public async Task BlockedDelivery_DoesNotStopFanout()
    {
        using var cancellation = new CancellationTokenSource();
        var deliveryEntered = NewSignal();
        var releaseDelivery = NewSignal();
        var fanoutCompleted = NewSignal();
        int fanoutCalls = 0;

        Task<int> RunFanout(CancellationToken token)
        {
            if (Interlocked.Increment(ref fanoutCalls) == 1)
            {
                fanoutCompleted.TrySetResult();
                return Task.FromResult(1);
            }

            return WaitForCancellation(token);
        }

        async Task<int> RunDelivery(CancellationToken token)
        {
            deliveryEntered.TrySetResult();
            await releaseDelivery.Task.WaitAsync(token);
            return 0;
        }

        using ILoggerFactory loggerFactory = LoggerFactory.Create(_ => { });
        var delay = new BlockingDelay();
        var fanout = new OutboxFanoutWorker(
            RunFanout,
            loggerFactory.CreateLogger<OutboxFanoutWorker>(),
            new FanoutLoopOptions(10, TimeSpan.FromSeconds(2)),
            delay);
        var delivery = new SubscriptionDeliveryWorker(
            RunDelivery,
            loggerFactory.CreateLogger<SubscriptionDeliveryWorker>(),
            new DeliveryLoopOptions(25, TimeSpan.FromSeconds(2)),
            delay);

        Task fanoutTask = fanout.RunAsync(cancellation.Token);
        Task deliveryTask = delivery.RunAsync(cancellation.Token);
        await deliveryEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await fanoutCompleted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(releaseDelivery.Task.IsCompleted);
        Assert.True(fanoutCalls >= 1);

        releaseDelivery.TrySetResult();
        await cancellation.CancelAsync();
        await Task.WhenAll(fanoutTask, deliveryTask).WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task FanoutFailure_DoesNotStopDelivery()
    {
        using var cancellation = new CancellationTokenSource();
        var deliveryFinalized = NewSignal();
        var deliveryQueue = new WorkerTransportAbstractionsTests.FakeSubscriptionDeliveryQueue
        {
            ClaimedItems = [WorkerTransportAbstractionsTests.MakeWorkItem()],
            FinalizationSignal = deliveryFinalized
        };
        var mediator = WorkerTransportAbstractionsTests.BuildMediator(services =>
        {
            services.AddSingleton<ISubscriptionDeliveryQueue>(deliveryQueue);
            services.AddSingleton<IDeliveryClient>(
                new WorkerTransportAbstractionsTests.FakeDeliveryClient(new DeliveryResult(true, 200)));
            services.AddSingleton<Integrios.Application.Transforms.ITransformEvaluator>(
                new WorkerTransportAbstractionsTests.FakeTransformEvaluator());
        });

        Task<int> RunFanout(CancellationToken _) =>
            Task.FromException<int>(new InvalidOperationException("fanout failed"));

        using ILoggerFactory loggerFactory = LoggerFactory.Create(_ => { });
        var delay = new BlockingDelay();
        var fanout = new OutboxFanoutWorker(
            RunFanout,
            loggerFactory.CreateLogger<OutboxFanoutWorker>(),
            new FanoutLoopOptions(10, TimeSpan.FromSeconds(2)),
            delay);
        var delivery = new SubscriptionDeliveryWorker(
            mediator,
            loggerFactory.CreateLogger<SubscriptionDeliveryWorker>(),
            new DeliveryLoopOptions(25, TimeSpan.FromSeconds(2)),
            delay);

        Task fanoutTask = fanout.RunAsync(cancellation.Token);
        Task deliveryTask = delivery.RunAsync(cancellation.Token);
        await deliveryFinalized.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(deliveryQueue.ClaimCallCount >= 1);
        DeliveryAttemptCompletion completion = Assert.Single(deliveryQueue.Completions);
        Assert.True(completion.Succeeded);
        Assert.True(delay.Calls >= 1);

        await cancellation.CancelAsync();
        await Task.WhenAll(fanoutTask, deliveryTask).WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task NonEmptyBatch_ContinuesImmediately_ThenEmptyBatchUsesConfiguredDelay()
    {
        using var cancellation = new CancellationTokenSource();
        int calls = 0;
        var delay = new CancellingDelay(cancellation);
        using ILoggerFactory loggerFactory = LoggerFactory.Create(_ => { });
        var worker = new OutboxFanoutWorker(
            _ => Task.FromResult(Interlocked.Increment(ref calls) == 1 ? 1 : 0),
            loggerFactory.CreateLogger<OutboxFanoutWorker>(),
            new FanoutLoopOptions(10, TimeSpan.FromMilliseconds(123)),
            delay);

        await worker.RunAsync(cancellation.Token);

        Assert.Equal(2, calls);
        Assert.Equal([TimeSpan.FromMilliseconds(123)], delay.Delays);
    }

    [Fact]
    public async Task ExceptionalIteration_LogsOnce_AndUsesOnlyItsConfiguredDelay()
    {
        using var cancellation = new CancellationTokenSource();
        var delay = new CancellingDelay(cancellation);
        var loggerProvider = new CapturingLoggerProvider();
        using ILoggerFactory loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(loggerProvider));
        var worker = new SubscriptionDeliveryWorker(
            _ => Task.FromException<int>(new InvalidOperationException("delivery failed")),
            loggerFactory.CreateLogger<SubscriptionDeliveryWorker>(),
            new DeliveryLoopOptions(25, TimeSpan.FromMilliseconds(456)),
            delay);

        await worker.RunAsync(cancellation.Token);

        Assert.Equal([TimeSpan.FromMilliseconds(456)], delay.Delays);
        Assert.Single(loggerProvider.Messages, message => message.Contains("Unhandled error", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Cancellation_ExitsCleanly_WithoutDelayOrErrorLog()
    {
        using var cancellation = new CancellationTokenSource();
        var delay = new RecordingDelay();
        var loggerProvider = new CapturingLoggerProvider();
        using ILoggerFactory loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(loggerProvider));
        var worker = new OutboxFanoutWorker(
            async token =>
            {
                await cancellation.CancelAsync();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return 0;
            },
            loggerFactory.CreateLogger<OutboxFanoutWorker>(),
            new FanoutLoopOptions(10, TimeSpan.FromSeconds(2)),
            delay);

        await worker.RunAsync(cancellation.Token);

        Assert.Empty(delay.Delays);
        Assert.DoesNotContain(loggerProvider.Messages, message => message.Contains("Unhandled error", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GenericHost_StopWaitsForClaimedDeliveryToFinalize_WhileFanoutRunsIndependently()
    {
        var fanout = new SignalingOutboxFanout();
        var deliveryClient = new BlockingDeliveryClient();
        var deliveryFinalized = NewSignal();
        var deliveryQueue = new WorkerTransportAbstractionsTests.FakeSubscriptionDeliveryQueue
        {
            ClaimedItems = [WorkerTransportAbstractionsTests.MakeWorkItem()],
            FinalizationSignal = deliveryFinalized
        };
        IMediator mediator = WorkerTransportAbstractionsTests.BuildMediator(services =>
        {
            services.AddSingleton<IOutboxFanout>(fanout);
            services.AddSingleton<ISubscriptionDeliveryQueue>(deliveryQueue);
            services.AddSingleton<IDeliveryClient>(deliveryClient);
            services.AddSingleton<ITransformEvaluator>(
                new WorkerTransportAbstractionsTests.FakeTransformEvaluator());
        });
        IConfiguration configuration = new ConfigurationBuilder().Build();
        using IHost host = Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton<ISender>(mediator);
                services.AddSingleton(DeliveryExecutionOptions.Default);
                services.AddWorkerHostServices(configuration, enableBackgroundLoops: true);
            })
            .Build();

        await host.StartAsync();
        await deliveryClient.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await fanout.Processed.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Task stopping = host.StopAsync();
        try
        {
            await Task.Yield();
            Assert.False(stopping.IsCompleted);

            deliveryClient.Release.TrySetResult();
            await stopping.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(deliveryFinalized.Task.IsCompleted);
        }
        finally
        {
            deliveryClient.Release.TrySetResult();
            await stopping.WaitAsync(TimeSpan.FromSeconds(2));
        }

        DeliveryAttemptCompletion completion = Assert.Single(deliveryQueue.Completions);
        Assert.True(completion.Succeeded);
    }

    [Fact]
    public async Task FanoutWorker_ForwardsConfiguredBatchSizeToCommand()
    {
        using var cancellation = new CancellationTokenSource();
        var sender = new RecordingSender();
        var delay = new CancellingDelay(cancellation);
        using ILoggerFactory loggerFactory = LoggerFactory.Create(_ => { });
        var worker = new OutboxFanoutWorker(
            sender,
            loggerFactory.CreateLogger<OutboxFanoutWorker>(),
            new FanoutLoopOptions(7, TimeSpan.FromSeconds(2)),
            delay);

        await worker.RunAsync(cancellation.Token);

        var command = Assert.IsType<ProcessOutboxBatchCommand>(Assert.Single(sender.Requests));
        Assert.Equal(7, command.BatchSize);
    }

    [Fact]
    public async Task DeliveryWorker_ForwardsConfiguredBatchSizeToCommand()
    {
        using var cancellation = new CancellationTokenSource();
        var sender = new RecordingSender();
        var delay = new CancellingDelay(cancellation);
        using ILoggerFactory loggerFactory = LoggerFactory.Create(_ => { });
        var worker = new SubscriptionDeliveryWorker(
            sender,
            loggerFactory.CreateLogger<SubscriptionDeliveryWorker>(),
            new DeliveryLoopOptions(13, TimeSpan.FromSeconds(2)),
            delay);

        await worker.RunAsync(cancellation.Token);

        var command = Assert.IsType<DispatchSubscriptionDeliveriesCommand>(Assert.Single(sender.Requests));
        Assert.Equal(13, command.BatchSize);
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task<int> WaitForCancellation(CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        return 0;
    }

    private sealed class BlockingDelay : IWorkerLoopDelay
    {
        public int Calls { get; private set; }

        public async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            Calls++;
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
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

    private sealed class SignalingOutboxFanout : IOutboxFanout
    {
        public TaskCompletionSource Processed { get; } = NewSignal();

        public Task<OutboxFanoutResult?> ProcessNextAsync(CancellationToken cancellationToken = default)
        {
            Processed.TrySetResult();
            return Task.FromResult<OutboxFanoutResult?>(null);
        }
    }

    private sealed class BlockingDeliveryClient : IDeliveryClient
    {
        public TaskCompletionSource Started { get; } = NewSignal();
        public TaskCompletionSource Release { get; } = NewSignal();

        public async Task<DeliveryResult> DeliverAsync(
            string url,
            string payloadJson,
            Action<HttpRequestMessage>? decorate = null,
            CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return new DeliveryResult(true, 200);
        }
    }

    private sealed class RecordingSender : ISender
    {
        public List<object> Requests { get; } = [];

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult((TResponse)(object)0);
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default)
            where TRequest : IRequest
        {
            Requests.Add(request);
            return Task.CompletedTask;
        }

        public Task<object?> Send(object request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult<object?>(0);
        }

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(
            IStreamRequest<TResponse> request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(
            object request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
