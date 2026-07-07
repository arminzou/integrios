using System.Diagnostics;
using System.Text.Json;
using Integrios.Application;
using Integrios.Application.Abstractions;
using Integrios.Application.Abstractions.Auth;
using Integrios.Application.Delivery;
using Integrios.Application.Outbox;
using Integrios.Application.Telemetry;
using Integrios.Domain.Events;
using Integrios.Infrastructure.Http.Auth;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
        Assert.Equal([(eventId, EventStatus.FannedOut, topicId)], eventBus.StatusUpdates);
    }

    [Fact]
    public async Task ProcessOutboxBatchCommand_NoMatchingSubscriptions_MarksUnroutedAndEmitsCounter()
    {
        using var metrics = new MetricCollector(IntegriosMetrics.MeterName);

        var messageId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var topicId = Guid.NewGuid();

        var eventBus = new FakeEventBus(
            [new EventBusMessage(messageId, eventId, 0)],
            new Dictionary<Guid, EventDetails>
            {
                [eventId] = new(eventId, Guid.NewGuid(), "payment.created", "{\"amount\":42}", topicId)
            });

        // The topic has a subscription, but none match the event type.
        var subscriptions = new FakeSubscriptionRepository(
            [
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
        Assert.Empty(queue.FanoutCalls);
        Assert.Equal([(eventId, EventStatus.Unrouted, topicId)], eventBus.StatusUpdates);
        Assert.Equal([messageId], eventBus.ProcessedMessageIds);

        var unrouted = Assert.Single(metrics.ForInstrument("integrios_events_unrouted"));
        Assert.Equal(1, unrouted.Value);
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
        Assert.Single(attempts.RecordedAttempts);
    }

    [Fact]
    public async Task DispatchSubscriptionDeliveriesCommand_OnSuccess_EmitsSucceededCounterAndDuration_WithIntegrationKey()
    {
        using var metrics = new MetricCollector(IntegriosMetrics.MeterName);
        const string integrationKey = "erp_system_success_metrics";

        var queue = new FakeSubscriptionDeliveryQueue
        {
            ClaimedItems = [MakeWorkItem(integrationKey: integrationKey)]
        };
        var mediator = BuildMediator(services =>
        {
            services.AddSingleton<ISubscriptionDeliveryQueue>(queue);
            services.AddSingleton<IDeliveryAttemptRepository>(new FakeDeliveryAttemptRepository());
            services.AddSingleton<IDeliveryClient>(new FakeDeliveryClient(new DeliveryResult(true, 200, null)));
            services.AddSingleton<ITransformEvaluator>(new FakeTransformEvaluator());
        });

        await mediator.Send(new DispatchSubscriptionDeliveriesCommand(25, 3));

        var succeeded = Assert.Single(
            metrics.ForInstrument("integrios_deliveries_succeeded"),
            measurement => Equals(measurement.Tag("integration_key"), integrationKey));
        Assert.Equal(1, succeeded.Value);
        Assert.Equal(integrationKey, succeeded.Tag("integration_key"));

        var duration = Assert.Single(
            metrics.ForInstrument("integrios_delivery_attempt_duration_seconds"),
            measurement => Equals(measurement.Tag("integration_key"), integrationKey));
        Assert.Equal("success", duration.Tag("result"));
        Assert.Equal(integrationKey, duration.Tag("integration_key"));
    }

    [Theory]
    [InlineData(500, false, "5xx")]
    [InlineData(404, false, "4xx")]
    [InlineData(0, true, "timeout")]
    [InlineData(0, false, "error")]
    public async Task DispatchSubscriptionDeliveriesCommand_OnTransientFailure_EmitsFailedCounter_WithStatusClass(
        int statusCode, bool isTimeout, string expectedClass)
    {
        using var metrics = new MetricCollector(IntegriosMetrics.MeterName);
        const string integrationKey = "erp_system_failed_metrics";

        var queue = new FakeSubscriptionDeliveryQueue
        {
            ClaimedItems = [MakeWorkItem(attemptCount: 0, integrationKey: integrationKey)]
        };
        var mediator = BuildMediator(services =>
        {
            services.AddSingleton<ISubscriptionDeliveryQueue>(queue);
            services.AddSingleton<IDeliveryAttemptRepository>(new FakeDeliveryAttemptRepository());
            services.AddSingleton<IDeliveryClient>(new FakeDeliveryClient(new DeliveryResult(false, statusCode, "boom", isTimeout)));
            services.AddSingleton<ITransformEvaluator>(new FakeTransformEvaluator());
        });

        await mediator.Send(new DispatchSubscriptionDeliveriesCommand(25, 3));

        var failed = Assert.Single(
            metrics.ForInstrument("integrios_deliveries_failed"),
            measurement => Equals(measurement.Tag("integration_key"), integrationKey));
        Assert.Equal(1, failed.Value);
        Assert.Equal(integrationKey, failed.Tag("integration_key"));
        Assert.Equal(expectedClass, failed.Tag("http_status_class"));
        Assert.DoesNotContain(
            metrics.ForInstrument("integrios_deliveries_dead_lettered"),
            measurement => Equals(measurement.Tag("integration_key"), integrationKey));
    }

    [Fact]
    public async Task DispatchSubscriptionDeliveriesCommand_OnMaxAttempts_EmitsDeadLetteredCounter_NotFailed()
    {
        using var metrics = new MetricCollector(IntegriosMetrics.MeterName);
        const string integrationKey = "erp_system_dead_letter_metrics";

        var queue = new FakeSubscriptionDeliveryQueue
        {
            ClaimedItems = [MakeWorkItem(attemptCount: 2, integrationKey: integrationKey)]
        };
        var mediator = BuildMediator(services =>
        {
            services.AddSingleton<ISubscriptionDeliveryQueue>(queue);
            services.AddSingleton<IDeliveryAttemptRepository>(new FakeDeliveryAttemptRepository());
            services.AddSingleton<IDeliveryClient>(new FakeDeliveryClient(new DeliveryResult(false, 500, "boom")));
            services.AddSingleton<ITransformEvaluator>(new FakeTransformEvaluator());
        });

        await mediator.Send(new DispatchSubscriptionDeliveriesCommand(25, 3));

        var deadLettered = Assert.Single(
            metrics.ForInstrument("integrios_deliveries_dead_lettered"),
            measurement => Equals(measurement.Tag("integration_key"), integrationKey));
        Assert.Equal(1, deadLettered.Value);
        Assert.Equal(integrationKey, deadLettered.Tag("integration_key"));
        Assert.DoesNotContain(
            metrics.ForInstrument("integrios_deliveries_failed"),
            measurement => Equals(measurement.Tag("integration_key"), integrationKey));
    }

    [Fact]
    public async Task DeliveryMetrics_NeverEmit_ForbiddenTenantControlledLabels()
    {
        using var metrics = new MetricCollector(IntegriosMetrics.MeterName);

        var queue = new FakeSubscriptionDeliveryQueue
        {
            ClaimedItems =
            [
                MakeWorkItem(integrationKey: "erp_system"),
                MakeWorkItem(integrationKey: "crm_system")
            ]
        };
        // First item succeeds, second fails — exercises both label sets.
        var deliveryClient = new SequenceDeliveryClient(
            new DeliveryResult(true, 200, null),
            new DeliveryResult(false, 503, "down"));
        var mediator = BuildMediator(services =>
        {
            services.AddSingleton<ISubscriptionDeliveryQueue>(queue);
            services.AddSingleton<IDeliveryAttemptRepository>(new FakeDeliveryAttemptRepository());
            services.AddSingleton<IDeliveryClient>(deliveryClient);
            services.AddSingleton<ITransformEvaluator>(new FakeTransformEvaluator());
        });

        await mediator.Send(new DispatchSubscriptionDeliveriesCommand(25, 3));

        string[] forbidden = ["tenant_id", "subscription_id", "connection_id"];
        Assert.DoesNotContain(metrics.AllTagKeys, key => forbidden.Contains(key));
    }

    [Fact]
    public async Task ProcessOutboxBatch_RestoresOutboxTraceparent_AsFanoutParent_AndPropagatesToDeliveries()
    {
        using var collector = new ActivityCollector(ActivitySources.ApplicationName);

        string outboxTraceparent;
        ActivityTraceId expectedTraceId;
        using (var acceptance = ActivitySources.Application.StartActivity("test.accept")!)
        {
            outboxTraceparent = acceptance.Id!;
            expectedTraceId = acceptance.TraceId;
        }

        var eventId = Guid.NewGuid();
        var topicId = Guid.NewGuid();
        var eventBus = new FakeEventBus(
            [new EventBusMessage(Guid.NewGuid(), eventId, 0, outboxTraceparent)],
            new Dictionary<Guid, EventDetails>
            {
                [eventId] = new(eventId, Guid.NewGuid(), "payment.created", "{\"amount\":42}", topicId)
            });
        var subscriptions = new FakeSubscriptionRepository(
            [new SubscriptionTarget(Guid.NewGuid(), "erp", ["payment.created"], Guid.NewGuid(), "https://erp.example/webhook", null)]);
        var queue = new FakeSubscriptionDeliveryQueue();
        var mediator = BuildMediator(services =>
        {
            services.AddSingleton<IEventBus>(eventBus);
            services.AddSingleton<ISubscriptionRepository>(subscriptions);
            services.AddSingleton<ISubscriptionDeliveryQueue>(queue);
        });

        await mediator.Send(new ProcessOutboxBatchCommand(10));

        var fanoutSpan = collector.Single("outbox.fanout");
        Assert.Equal(expectedTraceId, fanoutSpan.TraceId);

        // The fanout span's context is stamped onto the delivery rows it writes.
        var deliveryTraceparent = Assert.Single(queue.FanoutTraceparents);
        Assert.True(ActivityContext.TryParse(deliveryTraceparent, null, out var deliveryContext));
        Assert.Equal(expectedTraceId, deliveryContext.TraceId);
    }

    [Fact]
    public async Task DispatchSubscriptionDeliveries_KeepsSameTrace_AcrossDeliverAfterRetry()
    {
        using var collector = new ActivityCollector(ActivitySources.ApplicationName);

        string deliveryTraceparent;
        ActivityTraceId expectedTraceId;
        using (var fanout = ActivitySources.Application.StartActivity("test.fanout")!)
        {
            deliveryTraceparent = fanout.Id!;
            expectedTraceId = fanout.TraceId;
        }

        var deliveryId = Guid.NewGuid();

        // First attempt fails and schedules a retry.
        await SendDispatch(new FakeSubscriptionDeliveryQueue
        {
            ClaimedItems = [MakeWorkItem(deliveryId, attemptCount: 0, traceparent: deliveryTraceparent)]
        }, new DeliveryResult(false, 500, "boom"));

        // A later tick re-claims the row with the same stored traceparent.
        await SendDispatch(new FakeSubscriptionDeliveryQueue
        {
            ClaimedItems = [MakeWorkItem(deliveryId, attemptCount: 1, traceparent: deliveryTraceparent)]
        }, new DeliveryResult(true, 200, null));

        var deliverSpans = collector.Activities.Where(a => a.OperationName == "subscription.deliver").ToList();
        Assert.Equal(2, deliverSpans.Count);
        Assert.All(deliverSpans, span => Assert.Equal(expectedTraceId, span.TraceId));
    }

    [Fact]
    public async Task DispatchSubscriptionDeliveries_LogEntries_CarryDeliveryScopeKeys()
    {
        var capturing = new CapturingLoggerProvider();
        var queue = new FakeSubscriptionDeliveryQueue { ClaimedItems = [MakeWorkItem()] };
        var mediator = BuildMediator(services =>
        {
            services.AddSingleton<ISubscriptionDeliveryQueue>(queue);
            services.AddSingleton<IDeliveryAttemptRepository>(new FakeDeliveryAttemptRepository());
            services.AddSingleton<IDeliveryClient>(new FakeDeliveryClient(new DeliveryResult(true, 200, null)));
            services.AddSingleton<ITransformEvaluator>(new FakeTransformEvaluator());
            services.AddSingleton<ILoggerProvider>(capturing);
        });

        await mediator.Send(new DispatchSubscriptionDeliveriesCommand(25, 3));

        Assert.True(capturing.AnyEntryHasScopeKeys("event_id", "delivery_id", "subscription_id"));
    }

    private static async Task SendDispatch(FakeSubscriptionDeliveryQueue queue, DeliveryResult result)
    {
        var mediator = BuildMediator(services =>
        {
            services.AddSingleton<ISubscriptionDeliveryQueue>(queue);
            services.AddSingleton<IDeliveryAttemptRepository>(new FakeDeliveryAttemptRepository());
            services.AddSingleton<IDeliveryClient>(new FakeDeliveryClient(result));
            services.AddSingleton<ITransformEvaluator>(new FakeTransformEvaluator());
        });

        await mediator.Send(new DispatchSubscriptionDeliveriesCommand(25, 3));
    }

    private static SubscriptionDeliveryWorkItem MakeWorkItem(
    Guid? id = null,
    Guid? eventId = null,
    Guid? subscriptionId = null,
    Guid? destinationConnectionId = null,
    Guid? tenantId = null,
    int attemptCount = 0,
        string url = "https://erp.example/webhook",
        string payload = "{\"amount\":42}",
        string? transform = null,
        string integrationKey = "erp_system",
        string? traceparent = null) =>
        new(
    id ?? Guid.NewGuid(),
    eventId ?? Guid.NewGuid(),
    subscriptionId ?? Guid.NewGuid(),
    destinationConnectionId ?? Guid.NewGuid(),
    tenantId ?? Guid.NewGuid(),
    attemptCount,
            url,
            payload,
            "payment.created",
            "payments",
    DateTimeOffset.UtcNow,
    transform,
    integrationKey,
    null,
    traceparent);

    private static IMediator BuildMediator(Action<IServiceCollection> registerTestDoubles)
    {
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddIntegriosApplication();
    services.AddSingleton<IAuthSchemeRegistry>(new AuthSchemeRegistry([new ApiKeyHeaderAuthSchemeHandler(), new BearerTokenAuthSchemeHandler()]));
    services.AddSingleton<ISecretResolver>(new NullSecretResolver());
    registerTestDoubles(services);
    return services.BuildServiceProvider().GetRequiredService<IMediator>();
}

    private sealed class FakeEventBus(
        IReadOnlyList<EventBusMessage> claimedMessages,
        IReadOnlyDictionary<Guid, EventDetails> eventsById) : IEventBus
    {
        public List<Guid> ProcessedMessageIds { get; } = [];
        public List<(Guid EventId, EventStatus Status, Guid? TopicId)> StatusUpdates { get; } = [];

        public Task<IReadOnlyList<EventBusMessage>> ClaimBatchAsync(int limit, CancellationToken cancellationToken = default)
            => Task.FromResult(claimedMessages);

        public Task<EventDetails?> GetEventAsync(Guid eventId, CancellationToken cancellationToken = default)
            => Task.FromResult(eventsById.TryGetValue(eventId, out var ev) ? ev : null);

        public Task MarkProcessedAsync(Guid messageId, CancellationToken cancellationToken = default)
        {
            ProcessedMessageIds.Add(messageId);
            return Task.CompletedTask;
        }

        public Task UpdateEventStatusAsync(Guid eventId, EventStatus status, Guid? topicId, CancellationToken cancellationToken = default)
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
        public List<string?> FanoutTraceparents { get; } = [];
        public IReadOnlyList<SubscriptionDeliveryWorkItem> ClaimedItems { get; init; } = [];
        public List<Guid> SucceededIds { get; } = [];
        public List<(Guid DeliveryId, int AttemptCount, DateTimeOffset DeliverAfter)> ScheduledRetries { get; } = [];
        public List<Guid> DeadLetteredIds { get; } = [];

        public Task<int> FanoutAsync(Guid eventId, IReadOnlyList<SubscriptionFanoutTarget> targets, string? traceparent = null, CancellationToken cancellationToken = default)
        {
            FanoutCalls.Add((eventId, targets));
            FanoutTraceparents.Add(traceparent);
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

    public Task<DeliveryResult> DeliverAsync(string url, string payloadJson, Action<HttpRequestMessage>? decorate = null, CancellationToken cancellationToken = default)
    {
        _ = decorate;
        _ = cancellationToken;
        DeliveredUrls.Add(url);
        capturedPayloads?.Add(payloadJson);
        return Task.FromResult(result);
    }
    }

    private sealed class SequenceDeliveryClient(params DeliveryResult[] results) : IDeliveryClient
    {
        private int _index;

    public Task<DeliveryResult> DeliverAsync(string url, string payloadJson, Action<HttpRequestMessage>? decorate = null, CancellationToken cancellationToken = default)
    {
        _ = url;
        _ = payloadJson;
        _ = decorate;
        _ = cancellationToken;
        return Task.FromResult(results[_index++]);
    }
}

private sealed class NullSecretResolver : ISecretResolver
{
    public Task<string> ResolveAsync(Guid tenantId, string secretName, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException($"Unexpected secret lookup for '{secretName}'.");
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
