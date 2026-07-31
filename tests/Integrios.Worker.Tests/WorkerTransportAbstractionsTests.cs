using System.Diagnostics;
using System.Text.Json;
using Integrios.Application;
using Integrios.Application.Auth;
using Integrios.Application.Delivery;
using Integrios.Application.Outbox;
using Integrios.Application.Secrets;
using Integrios.Application.Telemetry;
using Integrios.Application.Transforms;
using Integrios.Domain.Delivery;
using Integrios.Domain.Events;
using Integrios.Infrastructure.Auth;
using Integrios.Infrastructure.Transforms;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MediatR;

namespace Integrios.Worker.Tests;

public sealed class WorkerTransportAbstractionsTests
{
    [Fact]
    public async Task ProcessOutboxBatchCommand_ProcessesCommittedFanoutResults()
    {
        var eventId = Guid.NewGuid();
        var topicId = Guid.NewGuid();
        var fanout = new FakeOutboxFanout(
            [new OutboxFanoutResult(eventId, topicId, EventStatus.FannedOut, 2, 2)]);
        var mediator = BuildMediator(services =>
        {
            services.AddSingleton<IOutboxFanout>(fanout);
        });

        var processedCount = await mediator.Send(new ProcessOutboxBatchCommand(10));

        Assert.Equal(1, processedCount);
        Assert.Equal(2, fanout.CallCount);
    }

    [Fact]
    public async Task ProcessOutboxBatchCommand_NoMatchingSubscriptions_MarksUnroutedAndEmitsCounter()
    {
        using var metrics = new MetricCollector(IntegriosMetrics.MeterName);

        var eventId = Guid.NewGuid();
        var topicId = Guid.NewGuid();
        var fanout = new FakeOutboxFanout(
            [new OutboxFanoutResult(eventId, topicId, EventStatus.Unrouted, 0, 0)]);
        var mediator = BuildMediator(services =>
        {
            services.AddSingleton<IOutboxFanout>(fanout);
        });

        var processedCount = await mediator.Send(new ProcessOutboxBatchCommand(10));

        Assert.Equal(1, processedCount);
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
            FailureDisposition = SubscriptionDeliveryDisposition.RetryScheduled,
            ClaimedItems =
            [
                MakeWorkItem(deliveryId, eventId, subscriptionId, destinationConnectionId,
                    url: "https://erp.example/webhook", payload: "{\"amount\":42}", transform: null)
            ]
        };

        var deliveryClient = new FakeDeliveryClient(new DeliveryResult(false, 500, "downstream exploded"));
        var mediator = BuildMediator(services =>
        {
            services.AddSingleton<ISubscriptionDeliveryQueue>(queue);
            services.AddSingleton<IDeliveryClient>(deliveryClient);
            services.AddSingleton<ITransformEvaluator>(new FakeTransformEvaluator());
        });

        var processedCount = await mediator.Send(new DispatchSubscriptionDeliveriesCommand(25));

        Assert.Equal(1, processedCount);
        DeliveryAttemptCompletion completion = Assert.Single(queue.Completions);
        Assert.Equal(deliveryId, completion.DeliveryId);
        Assert.Equal(queue.ClaimedItems[0].AttemptId, completion.AttemptId);
        Assert.False(completion.Succeeded);
        Assert.Equal(DeliveryFailurePhase.Http, completion.FailurePhase);
        Assert.Equal("{\"amount\":42}", completion.RequestPayloadJson);
        Assert.Equal(500, completion.ResponseStatusCode);
        Assert.Equal("downstream exploded", completion.ErrorMessage);
        Assert.Equal(SubscriptionDeliveryDisposition.RetryScheduled, Assert.Single(queue.Finalizations).Disposition);
    }

    [Fact]
    public async Task DispatchSubscriptionDeliveriesCommand_ClaimsJustInTime_UpToBatchSize()
    {
        var operations = new List<string>();
        var queue = new FakeSubscriptionDeliveryQueue
        {
            Operations = operations,
            ClaimedItems = [MakeWorkItem(), MakeWorkItem(), MakeWorkItem()]
        };
        var mediator = BuildMediator(services =>
        {
            services.AddSingleton<ISubscriptionDeliveryQueue>(queue);
            services.AddSingleton<IDeliveryClient>(new FakeDeliveryClient(new DeliveryResult(true, 200), operations: operations));
            services.AddSingleton<ITransformEvaluator>(new FakeTransformEvaluator());
        });

        int processedCount = await mediator.Send(new DispatchSubscriptionDeliveriesCommand(2));

        Assert.Equal(2, processedCount);
        Assert.Equal(2, queue.ClaimCallCount);
        Assert.Equal(2, queue.Completions.Count);
        Assert.Equal(["claim", "deliver", "finalize", "claim", "deliver", "finalize"], operations);
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

        var capturedPayloads = new List<string>();
        var deliveryClient = new FakeDeliveryClient(new DeliveryResult(true, 200, null), capturedPayloads);
        var mediator = BuildMediator(services =>
        {
            services.AddSingleton<ISubscriptionDeliveryQueue>(queue);
            services.AddSingleton<IDeliveryClient>(deliveryClient);
            services.AddSingleton<ITransformEvaluator>(new FakeTransformEvaluator());
        });

        await mediator.Send(new DispatchSubscriptionDeliveriesCommand(25));

        Assert.Single(capturedPayloads);
        Assert.Equal(payload, capturedPayloads[0]);
        DeliveryAttemptCompletion completion = Assert.Single(queue.Completions);
        Assert.True(completion.Succeeded);
        Assert.Null(completion.FailurePhase);
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

        var capturedPayloads = new List<string>();
        var deliveryClient = new FakeDeliveryClient(new DeliveryResult(true, 200, null), capturedPayloads);
        var evaluator = new FakeTransformEvaluator(transformedOutput);
        var mediator = BuildMediator(services =>
        {
            services.AddSingleton<ISubscriptionDeliveryQueue>(queue);
            services.AddSingleton<IDeliveryClient>(deliveryClient);
            services.AddSingleton<ITransformEvaluator>(evaluator);
        });

        await mediator.Send(new DispatchSubscriptionDeliveriesCommand(25));

        Assert.Single(capturedPayloads);
        Assert.Equal(transformedOutput, capturedPayloads[0]);
        Assert.True(Assert.Single(queue.Completions).Succeeded);
    }

    [Theory]
    [InlineData("xslt", "1")]
    [InlineData("jsonata", "2")]
    public async Task DispatchSubscriptionDeliveriesCommand_RejectsUnsupportedTransformSnapshot(
        string engine,
        string version)
    {
        string transformJson = JsonSerializer.Serialize(new
        {
            engine,
            version,
            expression = "amount"
        });
        var queue = new FakeSubscriptionDeliveryQueue
        {
            ClaimedItems = [MakeWorkItem(payload: "{\"amount\":42}", transform: transformJson)]
        };
        var deliveryClient = new FakeDeliveryClient(new DeliveryResult(true, 200));
        var mediator = BuildMediator(services =>
        {
            services.AddSingleton<ISubscriptionDeliveryQueue>(queue);
            services.AddSingleton<IDeliveryClient>(deliveryClient);
            services.AddSingleton<ITransformEvaluator, JsonataTransformEvaluator>();
        });

        await mediator.Send(new DispatchSubscriptionDeliveriesCommand(25));

        DeliveryAttemptCompletion completion = Assert.Single(queue.Completions);
        Assert.False(completion.Succeeded);
        Assert.Equal(DeliveryFailurePhase.Transform, completion.FailurePhase);
        Assert.Contains("Unsupported", completion.ErrorMessage);
        Assert.Empty(deliveryClient.DeliveredUrls);
    }

    [Fact]
    public async Task DispatchSubscriptionDeliveriesCommand_TransformFailure_UsesDeadLetterDispositionReportedByFinalization()
    {
        var deliveryId = Guid.NewGuid();
        var transformJson = """{"engine":"jsonata","version":"1","expression":"$.bad"}""";

        var queue = new FakeSubscriptionDeliveryQueue
        {
            FailureDisposition = SubscriptionDeliveryDisposition.DeadLettered,
            ClaimedItems =
            [
                MakeWorkItem(deliveryId, attemptNumber: 17, transform: transformJson)
            ]
        };

        var deliveryClient = new FakeDeliveryClient(new DeliveryResult(true, 200, null));
        var evaluator = new FakeTransformEvaluator(error: "evaluation failed");
        var mediator = BuildMediator(services =>
        {
            services.AddSingleton<ISubscriptionDeliveryQueue>(queue);
            services.AddSingleton<IDeliveryClient>(deliveryClient);
            services.AddSingleton<ITransformEvaluator>(evaluator);
        });

        await mediator.Send(new DispatchSubscriptionDeliveriesCommand(25));

        DeliveryAttemptCompletion completion = Assert.Single(queue.Completions);
        Assert.False(completion.Succeeded);
        Assert.Equal(DeliveryFailurePhase.Transform, completion.FailurePhase);
        Assert.Equal(SubscriptionDeliveryDisposition.DeadLettered, Assert.Single(queue.Finalizations).Disposition);
        Assert.Empty(deliveryClient.DeliveredUrls);
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
            services.AddSingleton<IDeliveryClient>(new FakeDeliveryClient(new DeliveryResult(true, 200, null)));
            services.AddSingleton<ITransformEvaluator>(new FakeTransformEvaluator());
        });

        await mediator.Send(new DispatchSubscriptionDeliveriesCommand(25));

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
            FailureDisposition = SubscriptionDeliveryDisposition.RetryScheduled,
            ClaimedItems = [MakeWorkItem(integrationKey: integrationKey)]
        };
        var mediator = BuildMediator(services =>
        {
            services.AddSingleton<ISubscriptionDeliveryQueue>(queue);
            services.AddSingleton<IDeliveryClient>(new FakeDeliveryClient(new DeliveryResult(false, statusCode, "boom", isTimeout)));
            services.AddSingleton<ITransformEvaluator>(new FakeTransformEvaluator());
        });

        await mediator.Send(new DispatchSubscriptionDeliveriesCommand(25));

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
    public async Task DispatchSubscriptionDeliveriesCommand_WhenFinalizationReportsDeadLettered_EmitsDeadLetteredCounter_NotFailed()
    {
        using var metrics = new MetricCollector(IntegriosMetrics.MeterName);
        const string integrationKey = "erp_system_dead_letter_metrics";

        var queue = new FakeSubscriptionDeliveryQueue
        {
            FailureDisposition = SubscriptionDeliveryDisposition.DeadLettered,
            ClaimedItems = [MakeWorkItem(attemptNumber: 17, integrationKey: integrationKey)]
        };
        var mediator = BuildMediator(services =>
        {
            services.AddSingleton<ISubscriptionDeliveryQueue>(queue);
            services.AddSingleton<IDeliveryClient>(new FakeDeliveryClient(new DeliveryResult(false, 500, "boom")));
            services.AddSingleton<ITransformEvaluator>(new FakeTransformEvaluator());
        });

        await mediator.Send(new DispatchSubscriptionDeliveriesCommand(25));

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
            FailureDisposition = SubscriptionDeliveryDisposition.RetryScheduled,
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
            services.AddSingleton<IDeliveryClient>(deliveryClient);
            services.AddSingleton<ITransformEvaluator>(new FakeTransformEvaluator());
        });

        await mediator.Send(new DispatchSubscriptionDeliveriesCommand(25));

        string[] forbidden = ["tenant_id", "subscription_id", "connection_id"];
        Assert.DoesNotContain(metrics.AllTagKeys, key => forbidden.Contains(key));
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
            FailureDisposition = SubscriptionDeliveryDisposition.RetryScheduled,
            ClaimedItems = [MakeWorkItem(deliveryId, attemptNumber: 1, traceparent: deliveryTraceparent)]
        }, new DeliveryResult(false, 500, "boom"));

        // A later tick re-claims the row with the same stored traceparent.
        await SendDispatch(new FakeSubscriptionDeliveryQueue
        {
            ClaimedItems = [MakeWorkItem(deliveryId, attemptNumber: 2, traceparent: deliveryTraceparent)]
        }, new DeliveryResult(true, 200, null));

        var deliverSpans = collector.Activities
            .Where(a => a.OperationName == "subscription.deliver" && Equals(a.GetTagItem("delivery_id"), deliveryId))
            .ToList();
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
            services.AddSingleton<IDeliveryClient>(new FakeDeliveryClient(new DeliveryResult(true, 200, null)));
            services.AddSingleton<ITransformEvaluator>(new FakeTransformEvaluator());
            services.AddSingleton<ILoggerProvider>(capturing);
        });

        await mediator.Send(new DispatchSubscriptionDeliveriesCommand(25));

        Assert.True(capturing.AnyEntryHasScopeKeys("event_id", "delivery_id", "subscription_id"));
    }

    [Fact]
    public async Task DispatchSubscriptionDeliveries_OwnershipLost_EmitsOnlyStaleFinalizationSignal()
    {
        using var metrics = new MetricCollector(IntegriosMetrics.MeterName);
        const string integrationKey = "ownership_lost_observability";
        var capturing = new CapturingLoggerProvider();
        Guid attemptId = Guid.NewGuid();
        var queue = new FakeSubscriptionDeliveryQueue
        {
            FinalizationStatus = DeliveryFinalizationStatus.OwnershipLost,
            ClaimedItems = [MakeWorkItem(attemptId: attemptId, integrationKey: integrationKey)]
        };
        var mediator = BuildMediator(services =>
        {
            services.AddSingleton<ISubscriptionDeliveryQueue>(queue);
            services.AddSingleton<IDeliveryClient>(new FakeDeliveryClient(new DeliveryResult(true, 200, null)));
            services.AddSingleton<ITransformEvaluator>(new FakeTransformEvaluator());
            services.AddSingleton<ILoggerProvider>(capturing);
        });

        await mediator.Send(new DispatchSubscriptionDeliveriesCommand(25));

        Assert.Equal(DeliveryFinalizationStatus.OwnershipLost, Assert.Single(queue.Finalizations).Status);
        Assert.Single(metrics.ForInstrument("integrios_delivery_stale_finalizations"));
        Assert.DoesNotContain(metrics.ForInstrument("integrios_deliveries_succeeded"), m => Equals(m.Tag("integration_key"), integrationKey));
        Assert.DoesNotContain(metrics.ForInstrument("integrios_deliveries_failed"), m => Equals(m.Tag("integration_key"), integrationKey));
        Assert.DoesNotContain(metrics.ForInstrument("integrios_deliveries_dead_lettered"), m => Equals(m.Tag("integration_key"), integrationKey));
        Assert.DoesNotContain(metrics.ForInstrument("integrios_delivery_attempt_duration_seconds"), m => Equals(m.Tag("integration_key"), integrationKey));
        Assert.True(capturing.AnyMessageContains(attemptId.ToString()));
    }

    [Fact]
    public async Task DispatchSubscriptionDeliveries_RecoveryDeadLetter_EmitsSignalAndContinuesToPendingWork()
    {
        using var metrics = new MetricCollector(IntegriosMetrics.MeterName);
        var capturing = new CapturingLoggerProvider();
        var recovered = new RecoveredSubscriptionDeliveryDeadLetter(
            Guid.NewGuid(),
            Guid.NewGuid(),
            3,
            Guid.NewGuid(),
            Guid.NewGuid(),
            "webhook");
        SubscriptionDeliveryWorkItem pending = MakeWorkItem(integrationKey: "webhook");
        var queue = new FakeSubscriptionDeliveryQueue
        {
            ClaimResults = [recovered, new ClaimedSubscriptionDelivery(pending)]
        };
        var deliveryClient = new FakeDeliveryClient(new DeliveryResult(true, 200));
        var mediator = BuildMediator(services =>
        {
            services.AddSingleton<ISubscriptionDeliveryQueue>(queue);
            services.AddSingleton<IDeliveryClient>(deliveryClient);
            services.AddSingleton<ITransformEvaluator>(new FakeTransformEvaluator());
            services.AddSingleton<ILoggerProvider>(capturing);
        });

        int processed = await mediator.Send(new DispatchSubscriptionDeliveriesCommand(25));

        Assert.Equal(1, processed);
        Assert.Single(deliveryClient.DeliveredUrls);
        Assert.Equal(3, queue.ClaimCallCount);
        var deadLettered = Assert.Single(
            metrics.ForInstrument("integrios_deliveries_dead_lettered"),
            measurement => Equals(measurement.Tag("integration_key"), "webhook"));
        Assert.Equal("webhook", deadLettered.Tag("integration_key"));
        Assert.True(capturing.AnyMessageContains(recovered.DeliveryId.ToString()));
        Assert.True(capturing.AnyMessageContains(recovered.AttemptId.ToString()));
        Assert.True(capturing.AnyMessageContains(recovered.EventId.ToString()));
        Assert.True(capturing.AnyMessageContains(recovered.SubscriptionId.ToString()));
    }

    [Fact]
    public async Task DispatchSubscriptionDeliveries_FinalizationFailure_AbandonsOnlyCurrentAttempt()
    {
        var queue = new FakeSubscriptionDeliveryQueue
        {
            ClaimedItems = [MakeWorkItem(), MakeWorkItem()],
            FinalizationExceptions = new Queue<Exception>(
                [new InvalidOperationException("injected finalization failure")])
        };
        var deliveryClient = new FakeDeliveryClient(new DeliveryResult(true, 200));
        var mediator = BuildMediator(services =>
        {
            services.AddSingleton<ISubscriptionDeliveryQueue>(queue);
            services.AddSingleton<IDeliveryClient>(deliveryClient);
            services.AddSingleton<ITransformEvaluator>(new FakeTransformEvaluator());
        });

        int processed = await mediator.Send(new DispatchSubscriptionDeliveriesCommand(2));

        Assert.Equal(2, processed);
        Assert.Equal(2, deliveryClient.DeliveredUrls.Count);
        Assert.Equal(2, queue.Completions.Count);
        Assert.Single(queue.Finalizations);
        Assert.Equal(queue.ClaimedItems[1].Id, queue.Completions[1].DeliveryId);
    }

    [Fact]
    public async Task DispatchSubscriptionDeliveries_AttemptDeadline_AbandonsOnlyCurrentAttempt()
    {
        var queue = new FakeSubscriptionDeliveryQueue
        {
            ClaimedItems = [MakeWorkItem(), MakeWorkItem()],
            HonorFinalizationCancellation = true
        };
        var deliveryClient = new DeadlineThenSuccessDeliveryClient();
        var mediator = BuildMediator(services =>
        {
            services.AddSingleton(new DeliveryExecutionOptions(
                TimeSpan.FromMilliseconds(10),
                TimeSpan.FromMilliseconds(50),
                TimeSpan.FromMinutes(2),
                TimeSpan.FromSeconds(1)));
            services.AddSingleton<ISubscriptionDeliveryQueue>(queue);
            services.AddSingleton<IDeliveryClient>(deliveryClient);
            services.AddSingleton<ITransformEvaluator>(new FakeTransformEvaluator());
        });

        int processed = await mediator.Send(new DispatchSubscriptionDeliveriesCommand(2));

        Assert.Equal(2, processed);
        Assert.Equal(2, deliveryClient.CallCount);
        Assert.Equal(2, queue.Completions.Count);
        Assert.Single(queue.Finalizations);
        Assert.Equal(queue.ClaimedItems[1].Id, queue.Completions[1].DeliveryId);
    }

    private static async Task SendDispatch(FakeSubscriptionDeliveryQueue queue, DeliveryResult result)
    {
        var mediator = BuildMediator(services =>
        {
            services.AddSingleton<ISubscriptionDeliveryQueue>(queue);
            services.AddSingleton<IDeliveryClient>(new FakeDeliveryClient(result));
            services.AddSingleton<ITransformEvaluator>(new FakeTransformEvaluator());
        });

        await mediator.Send(new DispatchSubscriptionDeliveriesCommand(25));
    }

    internal static SubscriptionDeliveryWorkItem MakeWorkItem(
        Guid? id = null,
        Guid? eventId = null,
        Guid? subscriptionId = null,
        Guid? destinationConnectionId = null,
        Guid? tenantId = null,
        Guid? attemptId = null,
        int attemptNumber = 1,
        string url = "https://erp.example/webhook",
        string payload = "{\"amount\":42}",
        string? transform = null,
        string integrationKey = "erp_system",
        string? traceparent = null) =>
        new(
            id ?? Guid.NewGuid(),
            attemptId ?? Guid.NewGuid(),
            attemptNumber,
            eventId ?? Guid.NewGuid(),
            subscriptionId ?? Guid.NewGuid(),
            destinationConnectionId ?? Guid.NewGuid(),
            tenantId ?? Guid.NewGuid(),
            "test-tenant",
            url,
            payload,
            "payment.created",
            "payments",
            DateTimeOffset.UtcNow,
            transform,
            integrationKey,
            null,
            traceparent);

    internal static IMediator BuildMediator(Action<IServiceCollection> registerTestDoubles)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddApplicationServices();
        services.AddSingleton(DeliveryExecutionOptions.Default);
        services.AddSingleton<IAuthSchemeRegistry>(new AuthSchemeRegistry([new ApiKeyHeaderAuthSchemeHandler(), new BearerTokenAuthSchemeHandler()]));
        services.AddSingleton<ISecretResolver>(new NullSecretResolver());
        registerTestDoubles(services);
        return services.BuildServiceProvider().GetRequiredService<IMediator>();
    }

    private sealed class FakeOutboxFanout(IReadOnlyList<OutboxFanoutResult> results) : IOutboxFanout
    {
        private int index;

        public int CallCount { get; private set; }

        public Task<OutboxFanoutResult?> ProcessNextAsync(CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult<OutboxFanoutResult?>(index < results.Count ? results[index++] : null);
        }
    }

    internal sealed class FakeSubscriptionDeliveryQueue : ISubscriptionDeliveryQueue
    {
        public IReadOnlyList<SubscriptionDeliveryWorkItem> ClaimedItems { get; init; } = [];
        public IReadOnlyList<SubscriptionDeliveryClaimResult>? ClaimResults { get; init; }
        public SubscriptionDeliveryDisposition FailureDisposition { get; set; } = SubscriptionDeliveryDisposition.RetryScheduled;
        public DeliveryFinalizationStatus FinalizationStatus { get; set; } = DeliveryFinalizationStatus.Applied;
        public List<DeliveryAttemptCompletion> Completions { get; } = [];
        public List<DeliveryFinalizationResult> Finalizations { get; } = [];
        public Queue<Exception> FinalizationExceptions { get; init; } = [];
        public bool HonorFinalizationCancellation { get; init; }
        public List<string>? Operations { get; init; }
        public TaskCompletionSource? FinalizationSignal { get; init; }
        public int ClaimCallCount { get; private set; }
        private int claimIndex;

        public Task<SubscriptionDeliveryClaimResult?> ClaimNextWithRecoveryAsync(CancellationToken cancellationToken = default)
        {
            ClaimCallCount++;
            Operations?.Add("claim");
            if (ClaimResults is not null)
            {
                return Task.FromResult<SubscriptionDeliveryClaimResult?>(
                    claimIndex < ClaimResults.Count ? ClaimResults[claimIndex++] : null);
            }

            return Task.FromResult<SubscriptionDeliveryClaimResult?>(
                claimIndex < ClaimedItems.Count
                    ? new ClaimedSubscriptionDelivery(ClaimedItems[claimIndex++])
                    : null);
        }

        public Task<DeliveryFinalizationResult> FinalizeAsync(DeliveryAttemptCompletion completion, CancellationToken cancellationToken = default)
        {
            Completions.Add(completion);
            Operations?.Add("finalize");
            FinalizationSignal?.TrySetResult();
            if (HonorFinalizationCancellation)
                cancellationToken.ThrowIfCancellationRequested();
            if (FinalizationExceptions.TryDequeue(out Exception? exception))
                throw exception;

            var disposition = completion.Succeeded ? SubscriptionDeliveryDisposition.Succeeded : FailureDisposition;
            var result = FinalizationStatus == DeliveryFinalizationStatus.Applied
                ? new DeliveryFinalizationResult(FinalizationStatus, disposition)
                : new DeliveryFinalizationResult(FinalizationStatus);
            Finalizations.Add(result);
            return Task.FromResult(result);
        }

    }

    internal sealed class FakeDeliveryClient(
        DeliveryResult result,
        List<string>? capturedPayloads = null,
        List<string>? operations = null) : IDeliveryClient
    {
        public List<string> DeliveredUrls { get; } = [];

        public Task<DeliveryResult> DeliverAsync(string url, string payloadJson, Action<HttpRequestMessage>? decorate = null, CancellationToken cancellationToken = default)
        {
            _ = decorate;
            _ = cancellationToken;
            operations?.Add("deliver");
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

    private sealed class DeadlineThenSuccessDeliveryClient : IDeliveryClient
    {
        public int CallCount { get; private set; }

        public async Task<DeliveryResult> DeliverAsync(
            string url,
            string payloadJson,
            Action<HttpRequestMessage>? decorate = null,
            CancellationToken cancellationToken = default)
        {
            _ = url;
            _ = payloadJson;
            _ = decorate;
            CallCount++;
            if (CallCount == 1)
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new DeliveryResult(true, 200);
        }
    }

    private sealed class NullSecretResolver : ISecretResolver
    {
        public string ProviderName => "test";

        public Task<string> ResolveAsync(TenantSecretScope tenant, string secretName, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException($"Unexpected secret lookup for '{secretName}'.");
    }

    internal sealed class FakeTransformEvaluator(string? output = null, string? error = null) : ITransformEvaluator
    {
        public string? ValidateExpression(TransformSpec transform) => null;

        public string Evaluate(
            TransformSpec transform,
            string payloadJson,
            TransformContext context)
        {
            if (error is not null)
                throw new TransformEvaluationException(error);
            return output ?? payloadJson;
        }
    }
}
