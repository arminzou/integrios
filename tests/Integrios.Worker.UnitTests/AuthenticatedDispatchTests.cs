using System.Text.Json;
using Integrios.Application;
using Integrios.Application.Auth;
using Integrios.Application.Delivery;
using Integrios.Application.Secrets;
using Integrios.Application.Telemetry;
using Integrios.Application.Transforms;
using Integrios.Domain.Entities;
using Integrios.Domain.Enums;
using Integrios.Domain.ValueObjects;
using Integrios.Infrastructure.Auth;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Integrios.Worker.UnitTests;

public sealed class AuthenticatedDispatchTests
{
    private static readonly JsonElement TenantApiKeyConfig =
        JsonSerializer.Deserialize<JsonElement>("""{"header_name":"X-Api-Key"}""");

    private static readonly JsonElement TenantApiKeySecretRefs =
        JsonSerializer.Deserialize<JsonElement>("""{"api_key":"erp_api_key"}""");

    private static readonly JsonElement EmptyObject = JsonSerializer.Deserialize<JsonElement>("{}");

    [Fact]
    public async Task Dispatch_ResolvesSecretsAndAppliesSelectedAuthScheme()
    {
        Guid deliveryId = Guid.NewGuid();
        Guid tenantId = Guid.NewGuid();
        var queue = new FakeEventDeliveryQueue
        {
            ClaimedItems =
            [
                MakeWorkItem(
                    id: deliveryId,
                    tenantId: tenantId,
                    auth: new ConnectionSchemeSelection
                    {
                        Scheme = "api_key_header",
                        Config = TenantApiKeyConfig,
                        SecretRefs = TenantApiKeySecretRefs
                    })
            ]
        };
        var deliveryClient = new CapturingDeliveryClient(new DeliveryResult(true, 200));
        var secretResolver = CreateSecretResolver(new Dictionary<string, string> { ["erp_api_key"] = "secret-value" });

        IMediator mediator = BuildMediator(services =>
        {
            services.AddSingleton<IEventDeliveryQueue>(queue);
            services.AddSingleton<IDeliveryClient>(deliveryClient);
            services.AddSingleton<ITransformEvaluator>(CreateTransformEvaluator());
            services.AddSingleton<IAuthSchemeRegistry>(CreateRegistry());
            services.AddSingleton<IDestinationAuthenticationSecretResolver>(secretResolver);
            services.AddSingleton<ILoggerProvider>(new CapturingLoggerProvider());
        });

        await mediator.Send(new DispatchEventDeliveriesCommand(25));

        await secretResolver.Received(1).ResolveAsync(
            Arg.Is<TenantSecretScope>(scope => scope.Id == tenantId && scope.Slug == "test-tenant"),
            "erp_api_key",
            Arg.Any<CancellationToken>());
        Assert.True(deliveryClient.Headers.TryGetValue("X-Api-Key", out string? headerValue));
        Assert.Equal("secret-value", headerValue);
        Assert.Equal(EventDeliveryDisposition.Succeeded, Assert.Single(queue.Finalizations).Disposition);
    }

    [Fact]
    public async Task Dispatch_ResolvedSecretValue_DoesNotLeakIntoAttemptsOrLogs()
    {
        const string resolvedSecret = "super-secret-value";
        var loggerProvider = new CapturingLoggerProvider();
        var queue = new FakeEventDeliveryQueue
        {
            ClaimedItems =
            [
                MakeWorkItem(
                    auth: new ConnectionSchemeSelection
                    {
                        Scheme = "api_key_header",
                        Config = TenantApiKeyConfig,
                        SecretRefs = TenantApiKeySecretRefs
                    })
            ]
        };

        IMediator mediator = BuildMediator(services =>
        {
            services.AddSingleton<IEventDeliveryQueue>(queue);
            services.AddSingleton<IDeliveryClient>(new CapturingDeliveryClient(new DeliveryResult(true, 200)));
            services.AddSingleton<ITransformEvaluator>(CreateTransformEvaluator());
            services.AddSingleton<IAuthSchemeRegistry>(CreateRegistry());
            services.AddSingleton<IDestinationAuthenticationSecretResolver>(CreateSecretResolver(new Dictionary<string, string> { ["erp_api_key"] = resolvedSecret }));
            services.AddSingleton<ILoggerProvider>(loggerProvider);
        });

        await mediator.Send(new DispatchEventDeliveriesCommand(25));

        DeliveryAttemptCompletion completion = Assert.Single(queue.Completions);
        Assert.DoesNotContain(resolvedSecret, completion.RequestPayloadJson!);
        Assert.DoesNotContain(resolvedSecret, completion.ErrorMessage ?? string.Empty);
        Assert.False(loggerProvider.AnyMessageContains(resolvedSecret));
    }

    [Fact]
    public async Task Dispatch_SecretWithTrailingNewline_FailsRequestConstructionWithoutLeaking()
    {
        const string resolvedSecret = "super-secret-value\n";
        const string connectorKey = "request_construction_observability";
        using var metrics = new MetricCollector(IntegriosMetrics.MeterName);
        var loggerProvider = new CapturingLoggerProvider();
        var queue = new FakeEventDeliveryQueue
        {
            ClaimedItems =
            [
                MakeWorkItem(
                    connectorKey: connectorKey,
                    auth: new ConnectionSchemeSelection
                    {
                        Scheme = "api_key_header",
                        Config = TenantApiKeyConfig,
                        SecretRefs = TenantApiKeySecretRefs
                    })
            ]
        };
        queue.FinalizationResult = Applied(EventDeliveryDisposition.RetryScheduled);

        IMediator mediator = BuildMediator(services =>
        {
            services.AddSingleton<IEventDeliveryQueue>(queue);
            services.AddSingleton<IDeliveryClient>(new CapturingDeliveryClient(new DeliveryResult(true, 200)));
            services.AddSingleton<ITransformEvaluator>(CreateTransformEvaluator());
            services.AddSingleton<IAuthSchemeRegistry>(CreateRegistry());
            services.AddSingleton<IDestinationAuthenticationSecretResolver>(CreateSecretResolver(new Dictionary<string, string> { ["erp_api_key"] = resolvedSecret }));
            services.AddSingleton<ILoggerProvider>(loggerProvider);
        });

        await mediator.Send(new DispatchEventDeliveriesCommand(25));

        DeliveryAttemptCompletion completion = Assert.Single(queue.Completions);
        Assert.False(completion.Succeeded);
        Assert.Equal(DeliveryFailurePhase.RequestConstruction, completion.FailurePhase);
        Assert.DoesNotContain("super-secret-value", completion.ErrorMessage ?? string.Empty);
        Assert.Equal(
            "Auth secret field 'api_key' contains a line break, which is not permitted in an HTTP header value.",
            completion.ErrorMessage);
        Assert.False(loggerProvider.AnyMessageContains("super-secret-value"));
        Assert.True(loggerProvider.AnyMessageContains("failure_phase=request_construction"));
        Assert.Single(
            metrics.ForInstrument("integrios_delivery_request_construction_failures"),
            measurement => Equals(measurement.Tag("connector_key"), connectorKey));
    }

    [Fact]
    public async Task Dispatch_UnexpectedPreparationError_ReplacesMessageWithoutLeakingValue()
    {
        const string resolvedSecret = "super-secret-value";
        var loggerProvider = new CapturingLoggerProvider();
        var queue = new FakeEventDeliveryQueue
        {
            FinalizationResult = Applied(EventDeliveryDisposition.RetryScheduled),
            ClaimedItems =
            [
                MakeWorkItem(
                    auth: new ConnectionSchemeSelection
                    {
                        Scheme = "leaky_scheme",
                        Config = EmptyObject,
                        SecretRefs = JsonSerializer.Deserialize<JsonElement>("""{"token":"erp_token"}""")
                    })
            ]
        };

        IMediator mediator = BuildMediator(services =>
        {
            services.AddSingleton<IEventDeliveryQueue>(queue);
            services.AddSingleton<IDeliveryClient>(new CapturingDeliveryClient(new DeliveryResult(true, 200)));
            services.AddSingleton<ITransformEvaluator>(CreateTransformEvaluator());
            services.AddSingleton<IAuthSchemeRegistry>(new AuthSchemeRegistry([new LeakyAuthSchemeHandler()]));
            services.AddSingleton<IDestinationAuthenticationSecretResolver>(CreateSecretResolver(new Dictionary<string, string> { ["erp_token"] = resolvedSecret }));
            services.AddSingleton<ILoggerProvider>(loggerProvider);
        });

        await mediator.Send(new DispatchEventDeliveriesCommand(25));

        DeliveryAttemptCompletion completion = Assert.Single(queue.Completions);
        Assert.Equal(DeliveryFailurePhase.RequestConstruction, completion.FailurePhase);
        Assert.Equal(DeliveryConfigurationException.GenericFailureMessage, completion.ErrorMessage);
        Assert.False(loggerProvider.AnyMessageContains(resolvedSecret));
    }

    [Fact]
    public async Task Dispatch_MissingSecret_SchedulesRetry()
    {
        using var metrics = new MetricCollector(IntegriosMetrics.MeterName);
        const string connectorKey = "secret_resolution_observability";
        Guid deliveryId = Guid.NewGuid();
        var loggerProvider = new CapturingLoggerProvider();
        var queue = new FakeEventDeliveryQueue
        {
            ClaimedItems =
            [
                MakeWorkItem(
                    id: deliveryId,
                    connectorKey: connectorKey,
                    auth: new ConnectionSchemeSelection
                    {
                        Scheme = "api_key_header",
                        Config = TenantApiKeyConfig,
                        SecretRefs = TenantApiKeySecretRefs
                    })
            ]
        };
        queue.FinalizationResult = Applied(EventDeliveryDisposition.RetryScheduled);

        IMediator mediator = BuildMediator(services =>
        {
            services.AddSingleton<IEventDeliveryQueue>(queue);
            services.AddSingleton<IDeliveryClient>(new CapturingDeliveryClient(new DeliveryResult(true, 200)));
            services.AddSingleton<ITransformEvaluator>(CreateTransformEvaluator());
            services.AddSingleton<IAuthSchemeRegistry>(CreateRegistry());
            services.AddSingleton<IDestinationAuthenticationSecretResolver>(CreateSecretResolver(new Dictionary<string, string>()));
            services.AddSingleton<ILoggerProvider>(loggerProvider);
        });

        await mediator.Send(new DispatchEventDeliveriesCommand(25));

        DeliveryAttemptCompletion completion = Assert.Single(queue.Completions);
        Assert.False(completion.Succeeded);
        Assert.Equal(DeliveryFailurePhase.SecretResolution, completion.FailurePhase);
        Assert.Equal(EventDeliveryDisposition.RetryScheduled, Assert.Single(queue.Finalizations).Disposition);
        Assert.True(loggerProvider.AnyMessageContains("failure_phase=secret_resolution"));
        Assert.Single(
            metrics.ForInstrument("integrios_delivery_secret_resolution_failures"),
            measurement => Equals(measurement.Tag("connector_key"), connectorKey));
    }

    [Fact]
    public async Task Dispatch_MissingSecret_UsesDeadLetterDispositionReportedByFinalization()
    {
        Guid deliveryId = Guid.NewGuid();
        var queue = new FakeEventDeliveryQueue
        {
            ClaimedItems =
            [
                MakeWorkItem(
                    id: deliveryId,
                    auth: new ConnectionSchemeSelection
                    {
                        Scheme = "api_key_header",
                        Config = TenantApiKeyConfig,
                        SecretRefs = TenantApiKeySecretRefs
                    })
            ]
        };
        queue.FinalizationResult = Applied(EventDeliveryDisposition.DeadLettered);

        IMediator mediator = BuildMediator(services =>
        {
            services.AddSingleton<IEventDeliveryQueue>(queue);
            services.AddSingleton<IDeliveryClient>(new CapturingDeliveryClient(new DeliveryResult(true, 200)));
            services.AddSingleton<ITransformEvaluator>(CreateTransformEvaluator());
            services.AddSingleton<IAuthSchemeRegistry>(CreateRegistry());
            services.AddSingleton<IDestinationAuthenticationSecretResolver>(CreateSecretResolver(new Dictionary<string, string>()));
            services.AddSingleton<ILoggerProvider>(new CapturingLoggerProvider());
        });

        await mediator.Send(new DispatchEventDeliveriesCommand(25));

        Assert.Equal(EventDeliveryDisposition.DeadLettered, Assert.Single(queue.Finalizations).Disposition);
    }

    [Fact]
    public async Task Dispatch_ReservedHeadersUseStableDeliveryAndClaimAttemptIdentities()
    {
        Guid deliveryId = Guid.NewGuid();
        Guid attemptId = Guid.NewGuid();
        Guid eventId = Guid.NewGuid();
        const int attemptNumber = 17;
        var deliveryClient = new CapturingDeliveryClient(new DeliveryResult(true, 200));
        var queue = new FakeEventDeliveryQueue
        {
            ClaimedItems =
            [
                MakeWorkItem(
                    id: deliveryId,
                    attemptId: attemptId,
                    attemptNumber: attemptNumber,
                    eventId: eventId,
                    auth: new ConnectionSchemeSelection
                    {
                        Scheme = "api_key_header",
                        Config = JsonSerializer.Deserialize<JsonElement>("""{"header_name":"Integrios-Event-Id"}"""),
                        SecretRefs = TenantApiKeySecretRefs
                    })
            ]
        };

        IMediator mediator = BuildMediator(services =>
        {
            services.AddSingleton<IEventDeliveryQueue>(queue);
            services.AddSingleton<IDeliveryClient>(deliveryClient);
            services.AddSingleton<ITransformEvaluator>(CreateTransformEvaluator());
            services.AddSingleton<IAuthSchemeRegistry>(CreateRegistry());
            services.AddSingleton<IDestinationAuthenticationSecretResolver>(CreateSecretResolver(new Dictionary<string, string> { ["erp_api_key"] = "cannot-overwrite" }));
        });

        await mediator.Send(new DispatchEventDeliveriesCommand(25));

        Assert.Equal(eventId.ToString(), deliveryClient.Headers["Integrios-Event-Id"]);
        Assert.Equal(deliveryId.ToString(), deliveryClient.Headers["Integrios-Delivery-Id"]);
        Assert.Equal(attemptId.ToString(), deliveryClient.Headers["Integrios-Attempt-Id"]);
        Assert.Equal("17", deliveryClient.Headers["Integrios-Attempt-Number"]);
    }

    [Fact]
    public async Task Dispatch_UnknownAuthScheme_FinalizesRequestConstructionFailure()
    {
        var queue = new FakeEventDeliveryQueue
        {
            FinalizationResult = Applied(EventDeliveryDisposition.RetryScheduled),
            ClaimedItems =
            [
                MakeWorkItem(
                    auth: new ConnectionSchemeSelection
                    {
                        Scheme = "unsupported",
                        Config = JsonSerializer.Deserialize<JsonElement>("{}"),
                        SecretRefs = JsonSerializer.Deserialize<JsonElement>("{}")
                    })
            ]
        };

        IMediator mediator = BuildMediator(services =>
        {
            services.AddSingleton<IEventDeliveryQueue>(queue);
            services.AddSingleton<IDeliveryClient>(new CapturingDeliveryClient(new DeliveryResult(true, 200)));
            services.AddSingleton<ITransformEvaluator>(CreateTransformEvaluator());
            services.AddSingleton<IAuthSchemeRegistry>(CreateRegistry());
            services.AddSingleton<IDestinationAuthenticationSecretResolver>(CreateSecretResolver(new Dictionary<string, string>()));
        });

        await mediator.Send(new DispatchEventDeliveriesCommand(25));

        DeliveryAttemptCompletion completion = Assert.Single(queue.Completions);
        Assert.False(completion.Succeeded);
        Assert.Equal(DeliveryFailurePhase.RequestConstruction, completion.FailurePhase);
    }

    [Fact]
    public async Task Dispatch_MalformedAuthSnapshot_FinalizesRequestConstructionFailure()
    {
        var queue = new FakeEventDeliveryQueue
        {
            FinalizationResult = Applied(EventDeliveryDisposition.RetryScheduled),
            ClaimedItems = [MakeWorkItem(authJson: "[]")]
        };
        var deliveryClient = new CapturingDeliveryClient(new DeliveryResult(true, 200));

        IMediator mediator = BuildMediator(services =>
        {
            services.AddSingleton<IEventDeliveryQueue>(queue);
            services.AddSingleton<IDeliveryClient>(deliveryClient);
            services.AddSingleton<ITransformEvaluator>(CreateTransformEvaluator());
            services.AddSingleton<IAuthSchemeRegistry>(CreateRegistry());
            services.AddSingleton<IDestinationAuthenticationSecretResolver>(CreateSecretResolver(new Dictionary<string, string>()));
        });

        await mediator.Send(new DispatchEventDeliveriesCommand(25));

        DeliveryAttemptCompletion completion = Assert.Single(queue.Completions);
        Assert.False(completion.Succeeded);
        Assert.Equal(DeliveryFailurePhase.RequestConstruction, completion.FailurePhase);
        Assert.Equal(0, deliveryClient.CallCount);
    }

    private static IMediator BuildMediator(Action<IServiceCollection> registerDoubles)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMetrics();
        services.AddApplicationServices();
        services.AddSingleton(DeliveryExecutionOptions.Default);
        registerDoubles(services);
        return services.BuildServiceProvider().GetRequiredService<IMediator>();
    }

    private static IAuthSchemeRegistry CreateRegistry() =>
        new AuthSchemeRegistry([new ApiKeyHeaderAuthSchemeHandler(), new BearerTokenAuthSchemeHandler()]);

    private static EventDeliveryWorkItem MakeWorkItem(
        Guid? id = null,
        Guid? attemptId = null,
        int attemptNumber = 1,
        Guid? eventId = null,
        Guid? subscriptionId = null,
        Guid? destinationConnectionId = null,
        Guid? tenantId = null,
        string connectorKey = "erp_system",
        ConnectionSchemeSelection? auth = null,
        string? authJson = null)
        => new(
            id ?? Guid.NewGuid(),
            attemptId ?? Guid.NewGuid(),
            attemptNumber,
            eventId ?? Guid.NewGuid(),
            subscriptionId ?? Guid.NewGuid(),
            destinationConnectionId ?? Guid.NewGuid(),
            tenantId ?? Guid.NewGuid(),
            "test-tenant",
            "{\"amount\":42}",
            "payment.created",
            "payments",
            DateTimeOffset.UtcNow,
            null,
            connectorKey,
            BuildSnapshotJson(auth, authJson),
            null);

    private static string BuildSnapshotJson(ConnectionSchemeSelection? auth, string? authJson)
    {
        string? destinationAuthSegment = authJson ?? (auth is null ? null : JsonSerializer.Serialize(auth, ConnectionSchemeSelection.StoredJson));
        string destinationAuthProperty = destinationAuthSegment is null ? "" : $"\"destination_authentication\":{destinationAuthSegment},";
        const string request = """{"version":1,"method":"POST","headers":{},"body":"json"}""";
        return "{\"version\":1,\"base_uri\":\"https://erp.example/webhook\"," + destinationAuthProperty + "\"request\":" + request + "}";
    }

    private sealed class FakeEventDeliveryQueue : IEventDeliveryQueue
    {
        public IReadOnlyList<EventDeliveryWorkItem> ClaimedItems { get; init; } = [];
        public DeliveryFinalizationResult FinalizationResult { get; set; } = Applied(EventDeliveryDisposition.Succeeded);
        public List<DeliveryAttemptCompletion> Completions { get; } = [];
        public List<DeliveryFinalizationResult> Finalizations { get; } = [];
        private int claimIndex;

        public Task<EventDeliveryClaimResult?> ClaimNextWithRecoveryAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<EventDeliveryClaimResult?>(
                claimIndex < ClaimedItems.Count
                    ? new ClaimedEventDelivery(ClaimedItems[claimIndex++])
                    : null);

        public Task<DeliveryFinalizationResult> FinalizeAsync(DeliveryAttemptCompletion completion, CancellationToken cancellationToken = default)
        {
            Completions.Add(completion);
            Finalizations.Add(FinalizationResult);
            return Task.FromResult(FinalizationResult);
        }

    }

    private static DeliveryFinalizationResult Applied(EventDeliveryDisposition disposition) =>
        new(DeliveryFinalizationStatus.Applied, disposition);

    // Mirrors framework header validation, which embeds the offending value in its message.
    private sealed class LeakyAuthSchemeHandler : IAuthSchemeHandler
    {
        public string Name => "leaky_scheme";
        public IReadOnlyList<string> RequiredConfigFields => [];
        public IReadOnlyList<string> RequiredSecretFields => ["token"];
        public IReadOnlyList<string> GetOwnedHeaderNames(JsonElement config) => ["Authorization"];

        public void Apply(IDictionary<string, string> headers, JsonElement config, IReadOnlyDictionary<string, string> secrets) =>
            throw new FormatException($"The format of value '{secrets["token"]}' is invalid.");
    }

    private sealed class CapturingDeliveryClient(DeliveryResult result) : IDeliveryClient
    {
        public Dictionary<string, string> Headers { get; } = [];
        public int CallCount { get; private set; }

        public Task<DeliveryResult> DeliverAsync(
            OutboundHttpMessage request, HttpSuccessRule? successRule, CancellationToken cancellationToken = default)
        {
            _ = successRule;
            CallCount++;
            foreach ((string name, string value) in request.Headers)
                Headers[name] = value;
            return Task.FromResult(result);
        }
    }

    private static IDestinationAuthenticationSecretResolver CreateSecretResolver(IReadOnlyDictionary<string, string> values)
    {
        var resolver = Substitute.For<IDestinationAuthenticationSecretResolver>();
        resolver.ProviderName.Returns("test");
        resolver.ResolveAsync(Arg.Any<TenantSecretScope>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                string secretName = callInfo.ArgAt<string>(1);
                if (values.TryGetValue(secretName, out string? value))
                    return Task.FromResult(value);

                throw new InvalidOperationException($"Secret reference '{secretName}' was not found.");
            });
        return resolver;
    }

    private static ITransformEvaluator CreateTransformEvaluator(string? output = null, string? error = null)
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
