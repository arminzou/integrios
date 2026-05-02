using System.Text.Json;
using Integrios.Application;
using Integrios.Application.Abstractions;
using Integrios.Application.Delivery;
using Integrios.Application.Outbox;
using Microsoft.Extensions.DependencyInjection;
using MediatR;

namespace Integrios.Worker.Tests;

public sealed class WorkerTransportAbstractionsTests
{
    [Fact]
    public async Task ProcessOutboxBatchCommand_FansOutMatchingSubscriptions_ThroughEventBusAndQueue()
    {
        var messageId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var topicId = Guid.NewGuid();
        var matchingSubscriptionId = Guid.NewGuid();
        var matchingConnectionId = Guid.NewGuid();

        var eventBus = new FakeEventBus(
            [new EventBusMessage(messageId, eventId, 0)],
            new Dictionary<Guid, EventDetails>
            {
                [eventId] = new(eventId, Guid.NewGuid(), "payment.created", "{\"amount\":42}", topicId)
            });

        var subscriptions = new FakeSubscriptionRepository(
            [
                new SubscriptionTarget(matchingSubscriptionId, "erp", ["payment.created"], matchingConnectionId, "https://erp.example/webhook", null),
                new SubscriptionTarget(Guid.NewGuid(), "crm", ["payment.updated"], Guid.NewGuid(), "https://crm.example/webhook", null)
            ]);

        var queue = new FakeSubscriptionDeliveryQueue();
        var mediator = BuildMediator(services =>
        {
            services.AddSingleton<IEventBus>(eventBus);
            services.AddSingleton<ISubscriptionRepository>(subscriptions);
            services.AddSingleton<ISubscriptionDeliveryQueue>(queue);
        });

        var processedCount = await mediator.Send(new ProcessOutboxBatchCommand(10));

        Assert.Equal(1, processedCount);
        Assert.Single(queue.FanoutCalls);
        Assert.Equal(eventId, queue.FanoutCalls[0].EventId);
        Assert.Single(queue.FanoutCalls[0].Targets);
        Assert.Equal(matchingSubscriptionId, queue.FanoutCalls[0].Targets[0].SubscriptionId);
        Assert.Equal(matchingConnectionId, queue.FanoutCalls[0].Targets[0].DestinationConnectionId);
        Assert.Equal([messageId], eventBus.ProcessedMessageIds);
        Assert.Equal([(eventId, "fanned_out", topicId)], eventBus.StatusUpdates);
    }

    [Fact]
    public async Task DispatchSubscriptionDeliveriesCommand_SchedulesRetry_ThroughSubscriptionDeliveryQueue()
    {
        var deliveryId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var subscriptionId = Guid.NewGuid();
        var destinationConnectionId = Guid.NewGuid();

        var queue = new FakeSubscriptionDeliveryQueue
        {
            ClaimedItems =
            [
                MakeWorkItem(deliveryId, eventId, subscriptionId, destinationConnectionId,
                    url: "https://erp.example/webhook", payload: "{\"amount\":42}", transform: null)
            ]
        };

        var attempts = new FakeDeliveryAttemptRepository();
        var deliveryClient = new FakeDeliveryClient(new DeliveryResult(false, 500, "downstream exploded"));
        var mediator = BuildMediator(services =>
        {
            services.AddSingleton<ISubscriptionDeliveryQueue>(queue);
            services.AddSingleton<IDeliveryAttemptRepository>(attempts);
            services.AddSingleton<IDeliveryClient>(deliveryClient);
            services.AddSingleton<ITransformEvaluator>(new FakeTransformEvaluator());
        });

        var processedCount = await mediator.Send(new DispatchSubscriptionDeliveriesCommand(25, 3));

        Assert.Equal(1, processedCount);
        Assert.Single(attempts.RecordedAttempts);
        Assert.Equal(deliveryId, queue.ScheduledRetries.Single().DeliveryId);
        Assert.Equal(1, queue.ScheduledRetries.Single().AttemptCount);
        Assert.Empty(queue.SucceededIds);
        Assert.Empty(queue.DeadLetteredIds);
    }

    [Fact]
    public async Task DispatchSubscriptionDeliveriesCommand_PassesThrough_WhenNoTransform()
    {
        var deliveryId = Guid.NewGuid();
        var payload = "{\"amount\":42}";

        var queue = new FakeSubscriptionDeliveryQueue
        {
            ClaimedItems =
            [
                MakeWorkItem(deliveryId, payload: payload, transform: null)
            ]
        };

        var attempts = new FakeDeliveryAttemptRepository();
        var capturedPayloads = new List<string>();
        var deliveryClient = new FakeDeliveryClient(new DeliveryResult(true, 200, null), capturedPayloads);
        var mediator = BuildMediator(services =>
        {
            services.AddSingleton<ISubscriptionDeliveryQueue>(queue);
            services.AddSingleton<IDeliveryAttemptRepository>(attempts);
            services.AddSingleton<IDeliveryClient>(deliveryClient);
            services.AddSingleton<ITransformEvaluator>(new FakeTransformEvaluator());
        });

        await mediator.Send(new DispatchSubscriptionDeliveriesCommand(25, 3));

        Assert.Single(capturedPayloads);
        Assert.Equal(payload, capturedPayloads[0]);
        Assert.Single(queue.SucceededIds);
    }

    [Fact]
    public async Task DispatchSubscriptionDeliveriesCommand_AppliesTransform_BeforeDelivery()
    {
        var deliveryId = Guid.NewGuid();
        var transformJson = """{"engine":"jsonata","version":"1","expression":"$.amount"}""";
        var transformedOutput = "42";

        var queue = new FakeSubscriptionDeliveryQueue
        {
            ClaimedItems =
            [
                MakeWorkItem(deliveryId, payload: "{\"amount\":42}", transform: transformJson)
            ]
        };

        var attempts = new FakeDeliveryAttemptRepository();
        var capturedPayloads = new List<string>();
        var deliveryClient = new FakeDeliveryClient(new DeliveryResult(true, 200, null), capturedPayloads);
        var evaluator = new FakeTransformEvaluator(transformedOutput);
        var mediator = BuildMediator(services =>
        {
            services.AddSingleton<ISubscriptionDeliveryQueue>(queue);
            services.AddSingleton<IDeliveryAttemptRepository>(attempts);
            services.AddSingleton<IDeliveryClient>(deliveryClient);
            services.AddSingleton<ITransformEvaluator>(evaluator);
        });

        await mediator.Send(new DispatchSubscriptionDeliveriesCommand(25, 3));

        Assert.Single(capturedPayloads);
        Assert.Equal(transformedOutput, capturedPayloads[0]);
        Assert.Single(queue.SucceededIds);
    }

    [Fact]
    public async Task DispatchSubscriptionDeliveriesCommand_DeadLetters_OnTransformFailure_WhenMaxAttemptsReached()
    {
        var deliveryId = Guid.NewGuid();
        var transformJson = """{"engine":"jsonata","version":"1","expression":"$.bad"}""";

        var queue = new FakeSubscriptionDeliveryQueue
        {
            ClaimedItems =
            [
                MakeWorkItem(deliveryId, attemptCount: 2, transform: transformJson)
            ]
        };

        var attempts = new FakeDeliveryAttemptRepository();
        var deliveryClient = new FakeDeliveryClient(new DeliveryResult(true, 200, null));
        var evaluator = new FakeTransformEvaluator(error: "evaluation failed");
        var mediator = BuildMediator(services =>
        {
            services.AddSingleton<ISubscriptionDeliveryQueue>(queue);
            services.AddSingleton<IDeliveryAttemptRepository>(attempts);
            services.AddSingleton<IDeliveryClient>(deliveryClient);
            services.AddSingleton<ITransformEvaluator>(evaluator);
        });

        await mediator.Send(new DispatchSubscriptionDeliveriesCommand(25, 3));

        Assert.Single(queue.DeadLetteredIds);
        Assert.Empty(queue.SucceededIds);
        Assert.Empty(deliveryClient.DeliveredUrls);
    }

    private static SubscriptionDeliveryWorkItem MakeWorkItem(
        Guid? id = null,
        Guid? eventId = null,
        Guid? subscriptionId = null,
        Guid? destinationConnectionId = null,
        int attemptCount = 0,
        string url = "https://erp.example/webhook",
        string payload = "{\"amount\":42}",
        string? transform = null) =>
        new(
            id ?? Guid.NewGuid(),
            eventId ?? Guid.NewGuid(),
            subscriptionId ?? Guid.NewGuid(),
            destinationConnectionId ?? Guid.NewGuid(),
            attemptCount,
            url,
            payload,
            "payment.created",
            "payments",
            DateTimeOffset.UtcNow,
            transform);

    private static IMediator BuildMediator(Action<IServiceCollection> registerTestDoubles)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIntegriosApplication();
        registerTestDoubles(services);
        return services.BuildServiceProvider().GetRequiredService<IMediator>();
    }

    private sealed class FakeEventBus(
        IReadOnlyList<EventBusMessage> claimedMessages,
        IReadOnlyDictionary<Guid, EventDetails> eventsById) : IEventBus
    {
        public List<Guid> ProcessedMessageIds { get; } = [];
        public List<(Guid EventId, string Status, Guid? TopicId)> StatusUpdates { get; } = [];

        public Task<IReadOnlyList<EventBusMessage>> ClaimBatchAsync(int limit, CancellationToken cancellationToken = default)
            => Task.FromResult(claimedMessages);

        public Task<EventDetails?> GetEventAsync(Guid eventId, CancellationToken cancellationToken = default)
            => Task.FromResult(eventsById.TryGetValue(eventId, out var ev) ? ev : null);

        public Task MarkProcessedAsync(Guid messageId, CancellationToken cancellationToken = default)
        {
            ProcessedMessageIds.Add(messageId);
            return Task.CompletedTask;
        }

        public Task UpdateEventStatusAsync(Guid eventId, string status, Guid? topicId, CancellationToken cancellationToken = default)
        {
            StatusUpdates.Add((eventId, status, topicId));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeSubscriptionRepository(IReadOnlyList<SubscriptionTarget> activeSubscriptions) : ISubscriptionRepository
    {
        public Task<IReadOnlyList<SubscriptionTarget>> GetActiveSubscriptionsAsync(Guid topicId, CancellationToken cancellationToken = default)
            => Task.FromResult(activeSubscriptions);

        public Task<Integrios.Domain.Topics.Subscription?> CreateAsync(Guid tenantId, Guid topicId, string name, JsonElement matchRules, Guid destinationConnectionId, JsonElement? transformConfig, bool dlqEnabled, int orderIndex, string? description, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<bool> DeactivateAsync(Guid tenantId, Guid topicId, Guid id, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<Integrios.Domain.Topics.Subscription?> GetByIdAsync(Guid tenantId, Guid topicId, Guid id, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<(IReadOnlyList<Integrios.Domain.Topics.Subscription> Items, string? NextCursor)> ListByTopicAsync(Guid tenantId, Guid topicId, string? afterCursor, int limit, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<Integrios.Domain.Topics.Subscription?> UpdateAsync(Guid tenantId, Guid topicId, Guid id, string name, JsonElement matchRules, Guid destinationConnectionId, JsonElement? transformConfig, bool dlqEnabled, int orderIndex, string? description, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class FakeSubscriptionDeliveryQueue : ISubscriptionDeliveryQueue
    {
        public List<(Guid EventId, IReadOnlyList<SubscriptionFanoutTarget> Targets)> FanoutCalls { get; } = [];
        public IReadOnlyList<SubscriptionDeliveryWorkItem> ClaimedItems { get; init; } = [];
        public List<Guid> SucceededIds { get; } = [];
        public List<(Guid DeliveryId, int AttemptCount, DateTimeOffset DeliverAfter)> ScheduledRetries { get; } = [];
        public List<Guid> DeadLetteredIds { get; } = [];

        public Task<int> FanoutAsync(Guid eventId, IReadOnlyList<SubscriptionFanoutTarget> targets, CancellationToken cancellationToken = default)
        {
            FanoutCalls.Add((eventId, targets));
            return Task.FromResult(targets.Count);
        }

        public Task<IReadOnlyList<SubscriptionDeliveryWorkItem>> ClaimBatchAsync(int limit, CancellationToken cancellationToken = default)
            => Task.FromResult(ClaimedItems);

        public Task MarkSucceededAsync(Guid deliveryId, CancellationToken cancellationToken = default)
        {
            SucceededIds.Add(deliveryId);
            return Task.CompletedTask;
        }

        public Task ScheduleRetryAsync(Guid deliveryId, int newAttemptCount, DateTimeOffset deliverAfter, CancellationToken cancellationToken = default)
        {
            ScheduledRetries.Add((deliveryId, newAttemptCount, deliverAfter));
            return Task.CompletedTask;
        }

        public Task MarkDeadLetteredAsync(Guid deliveryId, CancellationToken cancellationToken = default)
        {
            DeadLetteredIds.Add(deliveryId);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeDeliveryAttemptRepository : IDeliveryAttemptRepository
    {
        public List<(Guid EventId, Guid SubscriptionId, Guid DestinationConnectionId, int AttemptNumber, string Status)> RecordedAttempts { get; } = [];

        public Task<int> GetAttemptCountAsync(Guid eventId, Guid subscriptionId, CancellationToken cancellationToken = default)
            => Task.FromResult(RecordedAttempts.Count(x => x.EventId == eventId && x.SubscriptionId == subscriptionId));

        public Task RecordAsync(Guid eventId, Guid subscriptionId, Guid destinationConnectionId, int attemptNumber, string status, string requestPayloadJson, int? responseStatusCode, string? responseBody, string? errorMessage, DateTimeOffset startedAt, DateTimeOffset? completedAt, CancellationToken cancellationToken = default)
        {
            RecordedAttempts.Add((eventId, subscriptionId, destinationConnectionId, attemptNumber, status));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeDeliveryClient(DeliveryResult result, List<string>? capturedPayloads = null) : IDeliveryClient
    {
        public List<string> DeliveredUrls { get; } = [];

        public Task<DeliveryResult> DeliverAsync(string url, string payloadJson, CancellationToken cancellationToken = default)
        {
            DeliveredUrls.Add(url);
            capturedPayloads?.Add(payloadJson);
            return Task.FromResult(result);
        }
    }

    private sealed class FakeTransformEvaluator(string? output = null, string? error = null) : ITransformEvaluator
    {
        public string? ValidateExpression(string engine, string version, string expression) => null;

        public string Evaluate(string expression, string payloadJson, TransformContext context)
        {
            if (error is not null)
                throw new TransformEvaluationException(error);
            return output ?? payloadJson;
        }
    }
}
