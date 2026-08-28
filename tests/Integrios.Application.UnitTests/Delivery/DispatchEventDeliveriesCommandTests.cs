using System.Diagnostics;
using System.Text.Json;
using Integrios.Application;
using Integrios.Application.Delivery;
using Integrios.Application.Secrets;
using Integrios.Application.Telemetry;
using Integrios.Application.Transforms;
using Integrios.Domain.Entities;
using Integrios.Domain.Enums;
using Integrios.Tests.Shared;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using static Integrios.Tests.Shared.DeliveryTestDoubles;

namespace Integrios.Application.UnitTests;

public sealed class DispatchEventDeliveriesCommandTests
{
    [Fact]
    public async Task DispatchEventDeliveriesCommand_SchedulesRetry_ThroughEventDeliveryQueue()
    {
        var deliveryId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var subscriptionId = Guid.NewGuid();
        var destinationConnectionId = Guid.NewGuid();

        var queue = new FakeEventDeliveryQueue
        {
            FailureDisposition = EventDeliveryDisposition.RetryScheduled,
            ClaimedItems =
            [
                MakeWorkItem(deliveryId, eventId, subscriptionId, destinationConnectionId,
                    url: "https://erp.example/webhook", payload: "{\"amount\":42}", transform: null)
            ]
        };

        var deliveryClient = new FakeDeliveryClient(new DeliveryResult(false, 500, "downstream exploded"));
        var mediator = BuildMediator(services =>
        {
            services.AddSingleton<IEventDeliveryQueue>(queue);
            services.AddSingleton<IDeliveryClient>(deliveryClient);
            services.AddSingleton<ITransformEvaluator>(CreateTransformEvaluator());
        });

        var processedCount = await mediator.Send(new DispatchEventDeliveriesCommand(25));

        processedCount.ShouldBe(1);
        DeliveryAttemptCompletion completion = queue.Completions.ShouldHaveSingleItem();
        completion.DeliveryId.ShouldBe(deliveryId);
        completion.AttemptId.ShouldBe(queue.ClaimedItems[0].AttemptId);
        completion.Succeeded.ShouldBeFalse();
        completion.FailurePhase.ShouldBe(DeliveryFailurePhase.Http);
        completion.RequestPayloadJson.ShouldBe("{\"amount\":42}");
        completion.ResponseStatusCode.ShouldBe(500);
        completion.ErrorMessage.ShouldBe("downstream exploded");
        queue.Finalizations.ShouldHaveSingleItem().Disposition.ShouldBe(EventDeliveryDisposition.RetryScheduled);
    }

    [Fact]
    public async Task DispatchEventDeliveriesCommand_ClaimsJustInTime_UpToBatchSize()
    {
        var operations = new List<string>();
        var queue = new FakeEventDeliveryQueue
        {
            Operations = operations,
            ClaimedItems = [MakeWorkItem(), MakeWorkItem(), MakeWorkItem()]
        };
        var mediator = BuildMediator(services =>
        {
            services.AddSingleton<IEventDeliveryQueue>(queue);
            services.AddSingleton<IDeliveryClient>(new FakeDeliveryClient(new DeliveryResult(true, 200), operations: operations));
            services.AddSingleton<ITransformEvaluator>(CreateTransformEvaluator());
        });

        int processedCount = await mediator.Send(new DispatchEventDeliveriesCommand(2));

        processedCount.ShouldBe(2);
        queue.ClaimCallCount.ShouldBe(2);
        queue.Completions.Count.ShouldBe(2);
        operations.ShouldBe(["claim", "deliver", "finalize", "claim", "deliver", "finalize"]);
    }

    [Fact]
    public async Task DispatchEventDeliveriesCommand_PassesThrough_WhenNoTransform()
    {
        var deliveryId = Guid.NewGuid();
        var payload = "{\"amount\":42}";

        var queue = new FakeEventDeliveryQueue
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
            services.AddSingleton<IEventDeliveryQueue>(queue);
            services.AddSingleton<IDeliveryClient>(deliveryClient);
            services.AddSingleton<ITransformEvaluator>(CreateTransformEvaluator());
        });

        await mediator.Send(new DispatchEventDeliveriesCommand(25));

        capturedPayloads.ShouldHaveSingleItem();
        capturedPayloads[0].ShouldBe(payload);
        DeliveryAttemptCompletion completion = queue.Completions.ShouldHaveSingleItem();
        completion.Succeeded.ShouldBeTrue();
        completion.FailurePhase.ShouldBeNull();
    }

    [Fact]
    public async Task DispatchEventDeliveriesCommand_AppliesTransform_BeforeDelivery()
    {
        var deliveryId = Guid.NewGuid();
        var transformJson = """{"engine":"jsonata","version":"1","expression":"$.amount"}""";
        var transformedOutput = "42";

        var queue = new FakeEventDeliveryQueue
        {
            ClaimedItems =
            [
                MakeWorkItem(deliveryId, payload: "{\"amount\":42}", transform: transformJson)
            ]
        };

        var capturedPayloads = new List<string>();
        var deliveryClient = new FakeDeliveryClient(new DeliveryResult(true, 200, null), capturedPayloads);
        var evaluator = CreateTransformEvaluator(transformedOutput);
        var mediator = BuildMediator(services =>
        {
            services.AddSingleton<IEventDeliveryQueue>(queue);
            services.AddSingleton<IDeliveryClient>(deliveryClient);
            services.AddSingleton<ITransformEvaluator>(evaluator);
        });

        await mediator.Send(new DispatchEventDeliveriesCommand(25));

        capturedPayloads.ShouldHaveSingleItem();
        capturedPayloads[0].ShouldBe(transformedOutput);
        queue.Completions.ShouldHaveSingleItem().Succeeded.ShouldBeTrue();
    }

    [Theory]
    [InlineData("xslt", "1")]
    [InlineData("jsonata", "2")]
    public async Task DispatchEventDeliveriesCommand_RejectsUnsupportedTransformSnapshot(
        string engine,
        string version)
    {
        // Real engine rejection is proven by Infrastructure TransformEvaluatorTests; here the fake
        // reports an unsupported engine so the handler's failure path is exercised without the engine.
        string transformJson = JsonSerializer.Serialize(new
        {
            engine,
            version,
            expression = "amount"
        });
        var queue = new FakeEventDeliveryQueue
        {
            ClaimedItems = [MakeWorkItem(payload: "{\"amount\":42}", transform: transformJson)]
        };
        var deliveryClient = new FakeDeliveryClient(new DeliveryResult(true, 200));
        var mediator = BuildMediator(services =>
        {
            services.AddSingleton<IEventDeliveryQueue>(queue);
            services.AddSingleton<IDeliveryClient>(deliveryClient);
            services.AddSingleton<ITransformEvaluator>(CreateTransformEvaluator(error: "Unsupported engine"));
        });

        await mediator.Send(new DispatchEventDeliveriesCommand(25));

        DeliveryAttemptCompletion completion = queue.Completions.ShouldHaveSingleItem();
        completion.Succeeded.ShouldBeFalse();
        completion.FailurePhase.ShouldBe(DeliveryFailurePhase.Transform);
        completion.ErrorMessage!.ShouldContain("Unsupported", Case.Sensitive);
        deliveryClient.DeliveredUrls.ShouldBeEmpty();
    }

    [Fact]
    public async Task DispatchEventDeliveriesCommand_TransformFailure_UsesDeadLetterDispositionReportedByFinalization()
    {
        var deliveryId = Guid.NewGuid();
        var transformJson = """{"engine":"jsonata","version":"1","expression":"$.bad"}""";

        var queue = new FakeEventDeliveryQueue
        {
            FailureDisposition = EventDeliveryDisposition.DeadLettered,
            ClaimedItems =
            [
                MakeWorkItem(deliveryId, attemptNumber: 17, transform: transformJson)
            ]
        };

        var deliveryClient = new FakeDeliveryClient(new DeliveryResult(true, 200, null));
        var evaluator = CreateTransformEvaluator(error: "evaluation failed");
        var mediator = BuildMediator(services =>
        {
            services.AddSingleton<IEventDeliveryQueue>(queue);
            services.AddSingleton<IDeliveryClient>(deliveryClient);
            services.AddSingleton<ITransformEvaluator>(evaluator);
        });

        await mediator.Send(new DispatchEventDeliveriesCommand(25));

        DeliveryAttemptCompletion completion = queue.Completions.ShouldHaveSingleItem();
        completion.Succeeded.ShouldBeFalse();
        completion.FailurePhase.ShouldBe(DeliveryFailurePhase.Transform);
        queue.Finalizations.ShouldHaveSingleItem().Disposition.ShouldBe(EventDeliveryDisposition.DeadLettered);
        deliveryClient.DeliveredUrls.ShouldBeEmpty();
    }

    [Fact]
    public async Task DispatchEventDeliveriesCommand_OnSuccess_EmitsSucceededCounterAndDuration_WithConnectorKey()
    {
        using var metrics = new MetricCollector(IntegriosMetrics.MeterName);
        const string connectorKey = "erp_system_success_metrics";

        var queue = new FakeEventDeliveryQueue
        {
            ClaimedItems = [MakeWorkItem(connectorKey: connectorKey)]
        };
        var mediator = BuildMediator(services =>
        {
            services.AddSingleton<IEventDeliveryQueue>(queue);
            services.AddSingleton<IDeliveryClient>(new FakeDeliveryClient(new DeliveryResult(true, 200, null)));
            services.AddSingleton<ITransformEvaluator>(CreateTransformEvaluator());
        });

        await mediator.Send(new DispatchEventDeliveriesCommand(25));

        var succeeded = metrics.ForInstrument("integrios_deliveries_succeeded")
            .Where(measurement => Equals(measurement.Tag("connector_key"), connectorKey))
            .ShouldHaveSingleItem();
        succeeded.Value.ShouldBe(1);
        succeeded.Tag("connector_key").ShouldBe(connectorKey);

        var duration = metrics.ForInstrument("integrios_delivery_attempt_duration_seconds")
            .Where(measurement => Equals(measurement.Tag("connector_key"), connectorKey))
            .ShouldHaveSingleItem();
        duration.Tag("result").ShouldBe("success");
        duration.Tag("connector_key").ShouldBe(connectorKey);
    }

    [Theory]
    [InlineData(500, false, "5xx")]
    [InlineData(404, false, "4xx")]
    [InlineData(0, true, "timeout")]
    [InlineData(0, false, "error")]
    public async Task DispatchEventDeliveriesCommand_OnTransientFailure_EmitsFailedCounter_WithStatusClass(
        int statusCode, bool isTimeout, string expectedClass)
    {
        using var metrics = new MetricCollector(IntegriosMetrics.MeterName);
        const string connectorKey = "erp_system_failed_metrics";

        var queue = new FakeEventDeliveryQueue
        {
            FailureDisposition = EventDeliveryDisposition.RetryScheduled,
            ClaimedItems = [MakeWorkItem(connectorKey: connectorKey)]
        };
        var mediator = BuildMediator(services =>
        {
            services.AddSingleton<IEventDeliveryQueue>(queue);
            services.AddSingleton<IDeliveryClient>(new FakeDeliveryClient(new DeliveryResult(false, statusCode, "boom", isTimeout)));
            services.AddSingleton<ITransformEvaluator>(CreateTransformEvaluator());
        });

        await mediator.Send(new DispatchEventDeliveriesCommand(25));

        var failed = metrics.ForInstrument("integrios_deliveries_failed")
            .Where(measurement => Equals(measurement.Tag("connector_key"), connectorKey))
            .ShouldHaveSingleItem();
        failed.Value.ShouldBe(1);
        failed.Tag("connector_key").ShouldBe(connectorKey);
        failed.Tag("http_status_class").ShouldBe(expectedClass);
        metrics.ForInstrument("integrios_deliveries_dead_lettered")
            .ShouldNotContain(measurement => Equals(measurement.Tag("connector_key"), connectorKey));
    }

    [Fact]
    public async Task DispatchEventDeliveriesCommand_WhenFinalizationReportsDeadLettered_EmitsDeadLetteredCounter_NotFailed()
    {
        using var metrics = new MetricCollector(IntegriosMetrics.MeterName);
        const string connectorKey = "erp_system_dead_letter_metrics";

        var queue = new FakeEventDeliveryQueue
        {
            FailureDisposition = EventDeliveryDisposition.DeadLettered,
            ClaimedItems = [MakeWorkItem(attemptNumber: 17, connectorKey: connectorKey)]
        };
        var mediator = BuildMediator(services =>
        {
            services.AddSingleton<IEventDeliveryQueue>(queue);
            services.AddSingleton<IDeliveryClient>(new FakeDeliveryClient(new DeliveryResult(false, 500, "boom")));
            services.AddSingleton<ITransformEvaluator>(CreateTransformEvaluator());
        });

        await mediator.Send(new DispatchEventDeliveriesCommand(25));

        var deadLettered = metrics.ForInstrument("integrios_deliveries_dead_lettered")
            .Where(measurement => Equals(measurement.Tag("connector_key"), connectorKey))
            .ShouldHaveSingleItem();
        deadLettered.Value.ShouldBe(1);
        deadLettered.Tag("connector_key").ShouldBe(connectorKey);
        metrics.ForInstrument("integrios_deliveries_failed")
            .ShouldNotContain(measurement => Equals(measurement.Tag("connector_key"), connectorKey));
    }

    [Fact]
    public async Task DeliveryMetrics_NeverEmit_ForbiddenTenantControlledLabels()
    {
        using var metrics = new MetricCollector(IntegriosMetrics.MeterName);

        var queue = new FakeEventDeliveryQueue
        {
            FailureDisposition = EventDeliveryDisposition.RetryScheduled,
            ClaimedItems =
            [
                MakeWorkItem(connectorKey: "erp_system"),
                MakeWorkItem(connectorKey: "crm_system")
            ]
        };
        // First item succeeds, second fails — exercises both label sets.
        var deliveryClient = new SequenceDeliveryClient(
            new DeliveryResult(true, 200, null),
            new DeliveryResult(false, 503, "down"));
        var mediator = BuildMediator(services =>
        {
            services.AddSingleton<IEventDeliveryQueue>(queue);
            services.AddSingleton<IDeliveryClient>(deliveryClient);
            services.AddSingleton<ITransformEvaluator>(CreateTransformEvaluator());
        });

        await mediator.Send(new DispatchEventDeliveriesCommand(25));

        string[] forbidden = ["tenant_id", "subscription_id", "connection_id"];
        metrics.AllTagKeys.ShouldNotContain(key => forbidden.Contains(key));
    }

    [Fact]
    public async Task DispatchEventDeliveries_KeepsSameTrace_AcrossDeliverAfterRetry()
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
        await SendDispatch(new FakeEventDeliveryQueue
        {
            FailureDisposition = EventDeliveryDisposition.RetryScheduled,
            ClaimedItems = [MakeWorkItem(deliveryId, attemptNumber: 1, traceparent: deliveryTraceparent)]
        }, new DeliveryResult(false, 500, "boom"));

        // A later tick re-claims the row with the same stored traceparent.
        await SendDispatch(new FakeEventDeliveryQueue
        {
            ClaimedItems = [MakeWorkItem(deliveryId, attemptNumber: 2, traceparent: deliveryTraceparent)]
        }, new DeliveryResult(true, 200, null));

        var deliverSpans = collector.Activities
            .Where(a => a.OperationName == "delivery.attempt" && Equals(a.GetTagItem("integrios.delivery.id"), deliveryId))
            .ToList();
        deliverSpans.Count.ShouldBe(2);
        deliverSpans.ShouldContain(span => Equals(span.GetTagItem("integrios.failure_phase"), "http"));
        string[] retiredKeys = ["event_id", "subscription_id", "delivery_id", "attempt_id", "attempt_number", "connector_key"];
        deliverSpans.SelectMany(span => span.TagObjects).Select(tag => tag.Key)
            .ShouldNotContain(key => retiredKeys.Contains(key));
        foreach (var span in deliverSpans)
            span.TraceId.ShouldBe(expectedTraceId);

        var attemptIds = deliverSpans.Select(span => span.SpanId).ToHashSet();
        foreach (string name in new[] { "delivery.transform", "delivery.http", "delivery.finalize" })
        {
            Activity[] spans = collector.Activities.Where(span => span.OperationName == name).ToArray();
            spans.Length.ShouldBe(2);
            spans.ShouldAllBe(span => attemptIds.Contains(span.ParentSpanId));
        }
    }

    [Theory]
    [InlineData(DeliveryFailurePhase.Transform, "transform")]
    [InlineData(DeliveryFailurePhase.SecretResolution, "secret_resolution")]
    [InlineData(DeliveryFailurePhase.RequestConstruction, "request_construction")]
    [InlineData(DeliveryFailurePhase.Http, "http")]
    public async Task DispatchEventDeliveries_RecordsEveryPersistedFailurePhaseOnAttemptSpan(
        DeliveryFailurePhase failurePhase,
        string expectedTag)
    {
        using var collector = new ActivityCollector(ActivitySources.ApplicationName);
        var queue = new FakeEventDeliveryQueue { ClaimedItems = [MakeWorkItem()] };

        await SendDispatch(queue, new DeliveryResult(false, 0, "failed", FailurePhase: failurePhase));

        collector.Single("delivery.attempt").GetTagItem("integrios.failure_phase").ShouldBe(expectedTag);
        queue.Completions.ShouldHaveSingleItem().FailurePhase.ShouldBe(failurePhase);
    }

    [Fact]
    public async Task DispatchEventDeliveries_LogEntries_CarryDeliveryScopeKeys()
    {
        var capturing = new CapturingLoggerProvider();
        var queue = new FakeEventDeliveryQueue { ClaimedItems = [MakeWorkItem()] };
        var mediator = BuildMediator(services =>
        {
            services.AddSingleton<IEventDeliveryQueue>(queue);
            services.AddSingleton<IDeliveryClient>(new FakeDeliveryClient(new DeliveryResult(true, 200, null)));
            services.AddSingleton<ITransformEvaluator>(CreateTransformEvaluator());
            services.AddSingleton<ILoggerProvider>(capturing);
        });

        await mediator.Send(new DispatchEventDeliveriesCommand(25));

        capturing.AnyEntryHasScopeKeys("event_id", "delivery_id", "subscription_id").ShouldBeTrue();
    }

    [Fact]
    public async Task DispatchEventDeliveries_OwnershipLost_EmitsOnlyStaleFinalizationSignal()
    {
        using var metrics = new MetricCollector(IntegriosMetrics.MeterName);
        const string connectorKey = "ownership_lost_observability";
        var capturing = new CapturingLoggerProvider();
        Guid attemptId = Guid.NewGuid();
        var queue = new FakeEventDeliveryQueue
        {
            FinalizationStatus = DeliveryFinalizationStatus.OwnershipLost,
            ClaimedItems = [MakeWorkItem(attemptId: attemptId, connectorKey: connectorKey)]
        };
        var mediator = BuildMediator(services =>
        {
            services.AddSingleton<IEventDeliveryQueue>(queue);
            services.AddSingleton<IDeliveryClient>(new FakeDeliveryClient(new DeliveryResult(true, 200, null)));
            services.AddSingleton<ITransformEvaluator>(CreateTransformEvaluator());
            services.AddSingleton<ILoggerProvider>(capturing);
        });

        await mediator.Send(new DispatchEventDeliveriesCommand(25));

        queue.Finalizations.ShouldHaveSingleItem().Status.ShouldBe(DeliveryFinalizationStatus.OwnershipLost);
        metrics.ForInstrument("integrios_delivery_stale_finalizations").ShouldHaveSingleItem();
        metrics.ForInstrument("integrios_deliveries_succeeded").ShouldNotContain(m => Equals(m.Tag("connector_key"), connectorKey));
        metrics.ForInstrument("integrios_deliveries_failed").ShouldNotContain(m => Equals(m.Tag("connector_key"), connectorKey));
        metrics.ForInstrument("integrios_deliveries_dead_lettered").ShouldNotContain(m => Equals(m.Tag("connector_key"), connectorKey));
        metrics.ForInstrument("integrios_delivery_attempt_duration_seconds").ShouldNotContain(m => Equals(m.Tag("connector_key"), connectorKey));
        capturing.AnyMessageContains(attemptId.ToString()).ShouldBeTrue();
    }

    [Fact]
    public async Task DispatchEventDeliveries_RecoveryDeadLetter_EmitsSignalAndContinuesToPendingWork()
    {
        using var metrics = new MetricCollector(IntegriosMetrics.MeterName);
        var capturing = new CapturingLoggerProvider();
        var recovered = new RecoveredEventDeliveryDeadLetter(
            Guid.NewGuid(),
            Guid.NewGuid(),
            3,
            Guid.NewGuid(),
            Guid.NewGuid(),
            "webhook");
        EventDeliveryWorkItem pending = MakeWorkItem(connectorKey: "webhook");
        var queue = new FakeEventDeliveryQueue
        {
            ClaimResults = [recovered, new ClaimedEventDelivery(pending)]
        };
        var deliveryClient = new FakeDeliveryClient(new DeliveryResult(true, 200));
        var mediator = BuildMediator(services =>
        {
            services.AddSingleton<IEventDeliveryQueue>(queue);
            services.AddSingleton<IDeliveryClient>(deliveryClient);
            services.AddSingleton<ITransformEvaluator>(CreateTransformEvaluator());
            services.AddSingleton<ILoggerProvider>(capturing);
        });

        int processed = await mediator.Send(new DispatchEventDeliveriesCommand(25));

        processed.ShouldBe(1);
        deliveryClient.DeliveredUrls.ShouldHaveSingleItem();
        queue.ClaimCallCount.ShouldBe(3);
        var deadLettered = metrics.ForInstrument("integrios_deliveries_dead_lettered")
            .Where(measurement => Equals(measurement.Tag("connector_key"), "webhook"))
            .ShouldHaveSingleItem();
        deadLettered.Tag("connector_key").ShouldBe("webhook");
        capturing.AnyMessageContains(recovered.DeliveryId.ToString()).ShouldBeTrue();
        capturing.AnyMessageContains(recovered.AttemptId.ToString()).ShouldBeTrue();
        capturing.AnyMessageContains(recovered.EventId.ToString()).ShouldBeTrue();
        capturing.AnyMessageContains(recovered.SubscriptionId.ToString()).ShouldBeTrue();
    }

    [Fact]
    public async Task DispatchEventDeliveries_FinalizationFailure_AbandonsOnlyCurrentAttempt()
    {
        var queue = new FakeEventDeliveryQueue
        {
            ClaimedItems = [MakeWorkItem(), MakeWorkItem()],
            FinalizationExceptions = new Queue<Exception>(
                [new InvalidOperationException("injected finalization failure")])
        };
        var deliveryClient = new FakeDeliveryClient(new DeliveryResult(true, 200));
        var mediator = BuildMediator(services =>
        {
            services.AddSingleton<IEventDeliveryQueue>(queue);
            services.AddSingleton<IDeliveryClient>(deliveryClient);
            services.AddSingleton<ITransformEvaluator>(CreateTransformEvaluator());
        });

        int processed = await mediator.Send(new DispatchEventDeliveriesCommand(2));

        processed.ShouldBe(2);
        deliveryClient.DeliveredUrls.Count.ShouldBe(2);
        queue.Completions.Count.ShouldBe(2);
        queue.Finalizations.ShouldHaveSingleItem();
        queue.Completions[1].DeliveryId.ShouldBe(queue.ClaimedItems[1].Id);
    }

    [Fact]
    public async Task DispatchEventDeliveries_AttemptDeadline_AbandonsOnlyCurrentAttempt()
    {
        var queue = new FakeEventDeliveryQueue
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
            services.AddSingleton<IEventDeliveryQueue>(queue);
            services.AddSingleton<IDeliveryClient>(deliveryClient);
            services.AddSingleton<ITransformEvaluator>(CreateTransformEvaluator());
        });

        int processed = await mediator.Send(new DispatchEventDeliveriesCommand(2));

        processed.ShouldBe(2);
        deliveryClient.CallCount.ShouldBe(2);
        queue.Completions.Count.ShouldBe(2);
        queue.Finalizations.ShouldHaveSingleItem();
        queue.Completions[1].DeliveryId.ShouldBe(queue.ClaimedItems[1].Id);
    }

    private static async Task SendDispatch(FakeEventDeliveryQueue queue, DeliveryResult result)
    {
        var mediator = BuildMediator(services =>
        {
            services.AddSingleton<IEventDeliveryQueue>(queue);
            services.AddSingleton<IDeliveryClient>(new FakeDeliveryClient(result));
            services.AddSingleton<ITransformEvaluator>(CreateTransformEvaluator());
        });

        await mediator.Send(new DispatchEventDeliveriesCommand(25));
    }

    private static IMediator BuildMediator(Action<IServiceCollection> registerTestDoubles)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddApplicationServices();
        services.AddSingleton(DeliveryExecutionOptions.Default);
        services.AddSingleton<IDestinationAuthenticatorRegistry>(
            new FakeDestinationAuthenticatorRegistry(
                new FakeApiKeyHeaderAuthenticator(),
                new FakeBearerTokenAuthenticator()));
        services.AddSingleton<IDestinationAuthenticationSecretResolver>(new NullSecretResolver());
        registerTestDoubles(services);
        return services.BuildServiceProvider().GetRequiredService<IMediator>();
    }

    private sealed class SequenceDeliveryClient(params DeliveryResult[] results) : IDeliveryClient
    {
        private int _index;

        public Task<DeliveryResult> DeliverAsync(
            OutboundHttpMessage request, HttpSuccessRule? successRule, CancellationToken cancellationToken = default)
        {
            _ = request;
            _ = successRule;
            _ = cancellationToken;
            return Task.FromResult(results[_index++]);
        }
    }

    private sealed class DeadlineThenSuccessDeliveryClient : IDeliveryClient
    {
        public int CallCount { get; private set; }

        public async Task<DeliveryResult> DeliverAsync(
            OutboundHttpMessage request,
            HttpSuccessRule? successRule,
            CancellationToken cancellationToken = default)
        {
            _ = request;
            _ = successRule;
            CallCount++;
            if (CallCount == 1)
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new DeliveryResult(true, 200);
        }
    }

    internal static ITransformEvaluator CreateTransformEvaluator(string? output = null, string? error = null)
    {
        var evaluator = Substitute.For<ITransformEvaluator>();
        evaluator.ValidateExpression(Arg.Any<TransformSpec>()).Returns((string?)null);
        evaluator.Evaluate(Arg.Any<TransformSpec>(), Arg.Any<string>(), Arg.Any<TransformContext>())
            .Returns(callInfo =>
            {
                if (error is not null)
                    throw new TransformEvaluationException(error);

                return output ?? callInfo.ArgAt<string>(1);
            });
        return evaluator;
    }
}
