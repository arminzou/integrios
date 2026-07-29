using System.Net.Http.Headers;
using System.Text.Json;
using Integrios.Application;
using Integrios.Application.Auth;
using Integrios.Application.Delivery;
using Integrios.Application.Secrets;
using Integrios.Application.Telemetry;
using Integrios.Application.Transforms;
using Integrios.Domain.Delivery;
using Integrios.Domain.Integrations;
using Integrios.Infrastructure.Http.Auth;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Integrios.Worker.Tests;

public sealed class AuthenticatedDispatchTests
{
    private static readonly JsonElement ApiKeyConfig =
        JsonSerializer.Deserialize<JsonElement>("""{"header_name":"X-Api-Key"}""");

    private static readonly JsonElement ApiKeySecretRefs =
        JsonSerializer.Deserialize<JsonElement>("""{"api_key":"erp_api_key"}""");

    private static readonly JsonElement EmptyObject = JsonSerializer.Deserialize<JsonElement>("{}");

    [Fact]
    public async Task Dispatch_ResolvesSecretsAndAppliesSelectedAuthScheme()
    {
        Guid deliveryId = Guid.NewGuid();
        Guid tenantId = Guid.NewGuid();
        var queue = new FakeSubscriptionDeliveryQueue
        {
            ClaimedItems =
            [
                MakeWorkItem(
                    id: deliveryId,
                    tenantId: tenantId,
                    auth: new ConnectionAuth
                    {
                        Scheme = "api_key_header",
                        Config = ApiKeyConfig,
                        SecretRefs = ApiKeySecretRefs
                    })
            ]
        };
        var deliveryClient = new CapturingDeliveryClient(new DeliveryResult(true, 200));
        var secretResolver = new FakeSecretResolver(new Dictionary<string, string> { ["erp_api_key"] = "secret-value" });

        IMediator mediator = BuildMediator(services =>
        {
            services.AddSingleton<ISubscriptionDeliveryQueue>(queue);
            services.AddSingleton<IDeliveryClient>(deliveryClient);
            services.AddSingleton<ITransformEvaluator>(new FakeTransformEvaluator());
            services.AddSingleton<IAuthSchemeRegistry>(CreateRegistry());
            services.AddSingleton<ISecretResolver>(secretResolver);
            services.AddSingleton<ILoggerProvider>(new CapturingLoggerProvider());
        });

        await mediator.Send(new DispatchSubscriptionDeliveriesCommand(25));

        Assert.Equal(["erp_api_key"], secretResolver.RequestedSecretNames);
        Assert.Equal(tenantId, Assert.Single(secretResolver.RequestedScopes).Id);
        Assert.Equal("test-tenant", Assert.Single(secretResolver.RequestedScopes).Slug);
        Assert.True(deliveryClient.Headers.TryGetValue("X-Api-Key", out string? headerValue));
        Assert.Equal("secret-value", headerValue);
        Assert.Equal(SubscriptionDeliveryDisposition.Succeeded, Assert.Single(queue.Finalizations).Disposition);
    }

    [Fact]
    public async Task Dispatch_ResolvedSecretValue_DoesNotLeakIntoAttemptsOrLogs()
    {
        const string resolvedSecret = "super-secret-value";
        var loggerProvider = new CapturingLoggerProvider();
        var queue = new FakeSubscriptionDeliveryQueue
        {
            ClaimedItems =
            [
                MakeWorkItem(
                    auth: new ConnectionAuth
                    {
                        Scheme = "api_key_header",
                        Config = ApiKeyConfig,
                        SecretRefs = ApiKeySecretRefs
                    })
            ]
        };

        IMediator mediator = BuildMediator(services =>
        {
            services.AddSingleton<ISubscriptionDeliveryQueue>(queue);
            services.AddSingleton<IDeliveryClient>(new CapturingDeliveryClient(new DeliveryResult(true, 200)));
            services.AddSingleton<ITransformEvaluator>(new FakeTransformEvaluator());
            services.AddSingleton<IAuthSchemeRegistry>(CreateRegistry());
            services.AddSingleton<ISecretResolver>(new FakeSecretResolver(new Dictionary<string, string> { ["erp_api_key"] = resolvedSecret }));
            services.AddSingleton<ILoggerProvider>(loggerProvider);
        });

        await mediator.Send(new DispatchSubscriptionDeliveriesCommand(25));

        DeliveryAttemptCompletion completion = Assert.Single(queue.Completions);
        Assert.DoesNotContain(resolvedSecret, completion.RequestPayloadJson!);
        Assert.DoesNotContain(resolvedSecret, completion.ErrorMessage ?? string.Empty);
        Assert.False(loggerProvider.AnyMessageContains(resolvedSecret));
    }

    [Fact]
    public async Task Dispatch_SecretWithTrailingNewline_FailsRequestConstructionWithoutLeaking()
    {
        const string resolvedSecret = "super-secret-value\n";
        const string integrationKey = "request_construction_observability";
        using var metrics = new MetricCollector(IntegriosMetrics.MeterName);
        var loggerProvider = new CapturingLoggerProvider();
        var queue = new FakeSubscriptionDeliveryQueue
        {
            ClaimedItems =
            [
                MakeWorkItem(
                    integrationKey: integrationKey,
                    auth: new ConnectionAuth
                    {
                        Scheme = "api_key_header",
                        Config = ApiKeyConfig,
                        SecretRefs = ApiKeySecretRefs
                    })
            ]
        };
        queue.FinalizationResult = Applied(SubscriptionDeliveryDisposition.RetryScheduled);

        IMediator mediator = BuildMediator(services =>
        {
            services.AddSingleton<ISubscriptionDeliveryQueue>(queue);
            services.AddSingleton<IDeliveryClient>(new CapturingDeliveryClient(new DeliveryResult(true, 200)));
            services.AddSingleton<ITransformEvaluator>(new FakeTransformEvaluator());
            services.AddSingleton<IAuthSchemeRegistry>(CreateRegistry());
            services.AddSingleton<ISecretResolver>(new FakeSecretResolver(new Dictionary<string, string> { ["erp_api_key"] = resolvedSecret }));
            services.AddSingleton<ILoggerProvider>(loggerProvider);
        });

        await mediator.Send(new DispatchSubscriptionDeliveriesCommand(25));

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
            measurement => Equals(measurement.Tag("integration_key"), integrationKey));
    }

    [Fact]
    public async Task Dispatch_UnexpectedPreparationError_ReplacesMessageWithoutLeakingValue()
    {
        const string resolvedSecret = "super-secret-value";
        var loggerProvider = new CapturingLoggerProvider();
        var queue = new FakeSubscriptionDeliveryQueue
        {
            FinalizationResult = Applied(SubscriptionDeliveryDisposition.RetryScheduled),
            ClaimedItems =
            [
                MakeWorkItem(
                    auth: new ConnectionAuth
                    {
                        Scheme = "leaky_scheme",
                        Config = EmptyObject,
                        SecretRefs = JsonSerializer.Deserialize<JsonElement>("""{"token":"erp_token"}""")
                    })
            ]
        };

        IMediator mediator = BuildMediator(services =>
        {
            services.AddSingleton<ISubscriptionDeliveryQueue>(queue);
            services.AddSingleton<IDeliveryClient>(new CapturingDeliveryClient(new DeliveryResult(true, 200)));
            services.AddSingleton<ITransformEvaluator>(new FakeTransformEvaluator());
            services.AddSingleton<IAuthSchemeRegistry>(new AuthSchemeRegistry([new LeakyAuthSchemeHandler()]));
            services.AddSingleton<ISecretResolver>(new FakeSecretResolver(new Dictionary<string, string> { ["erp_token"] = resolvedSecret }));
            services.AddSingleton<ILoggerProvider>(loggerProvider);
        });

        await mediator.Send(new DispatchSubscriptionDeliveriesCommand(25));

        DeliveryAttemptCompletion completion = Assert.Single(queue.Completions);
        Assert.Equal(DeliveryFailurePhase.RequestConstruction, completion.FailurePhase);
        Assert.Equal(DeliveryConfigurationException.GenericFailureMessage, completion.ErrorMessage);
        Assert.False(loggerProvider.AnyMessageContains(resolvedSecret));
    }

    [Fact]
    public async Task Dispatch_MissingSecret_SchedulesRetry()
    {
        using var metrics = new MetricCollector(IntegriosMetrics.MeterName);
        const string integrationKey = "secret_resolution_observability";
        Guid deliveryId = Guid.NewGuid();
        var loggerProvider = new CapturingLoggerProvider();
        var queue = new FakeSubscriptionDeliveryQueue
        {
            ClaimedItems =
            [
                MakeWorkItem(
                    id: deliveryId,
                    integrationKey: integrationKey,
                    auth: new ConnectionAuth
                    {
                        Scheme = "api_key_header",
                        Config = ApiKeyConfig,
                        SecretRefs = ApiKeySecretRefs
                    })
            ]
        };
        queue.FinalizationResult = Applied(SubscriptionDeliveryDisposition.RetryScheduled);

        IMediator mediator = BuildMediator(services =>
        {
            services.AddSingleton<ISubscriptionDeliveryQueue>(queue);
            services.AddSingleton<IDeliveryClient>(new CapturingDeliveryClient(new DeliveryResult(true, 200)));
            services.AddSingleton<ITransformEvaluator>(new FakeTransformEvaluator());
            services.AddSingleton<IAuthSchemeRegistry>(CreateRegistry());
            services.AddSingleton<ISecretResolver>(new FakeSecretResolver(new Dictionary<string, string>()));
            services.AddSingleton<ILoggerProvider>(loggerProvider);
        });

        await mediator.Send(new DispatchSubscriptionDeliveriesCommand(25));

        DeliveryAttemptCompletion completion = Assert.Single(queue.Completions);
        Assert.False(completion.Succeeded);
        Assert.Equal(DeliveryFailurePhase.SecretResolution, completion.FailurePhase);
        Assert.Equal(SubscriptionDeliveryDisposition.RetryScheduled, Assert.Single(queue.Finalizations).Disposition);
        Assert.True(loggerProvider.AnyMessageContains("failure_phase=secret_resolution"));
        Assert.Single(
            metrics.ForInstrument("integrios_delivery_secret_resolution_failures"),
            measurement => Equals(measurement.Tag("integration_key"), integrationKey));
    }

    [Fact]
    public async Task Dispatch_MissingSecret_UsesDeadLetterDispositionReportedByFinalization()
    {
        Guid deliveryId = Guid.NewGuid();
        var queue = new FakeSubscriptionDeliveryQueue
        {
            ClaimedItems =
            [
                MakeWorkItem(
                    id: deliveryId,
                    auth: new ConnectionAuth
                    {
                        Scheme = "api_key_header",
                        Config = ApiKeyConfig,
                        SecretRefs = ApiKeySecretRefs
                    })
            ]
        };
        queue.FinalizationResult = Applied(SubscriptionDeliveryDisposition.DeadLettered);

        IMediator mediator = BuildMediator(services =>
        {
            services.AddSingleton<ISubscriptionDeliveryQueue>(queue);
            services.AddSingleton<IDeliveryClient>(new CapturingDeliveryClient(new DeliveryResult(true, 200)));
            services.AddSingleton<ITransformEvaluator>(new FakeTransformEvaluator());
            services.AddSingleton<IAuthSchemeRegistry>(CreateRegistry());
            services.AddSingleton<ISecretResolver>(new FakeSecretResolver(new Dictionary<string, string>()));
            services.AddSingleton<ILoggerProvider>(new CapturingLoggerProvider());
        });

        await mediator.Send(new DispatchSubscriptionDeliveriesCommand(25));

        Assert.Equal(SubscriptionDeliveryDisposition.DeadLettered, Assert.Single(queue.Finalizations).Disposition);
    }

    [Fact]
    public async Task Dispatch_ReservedHeadersUseStableDeliveryAndClaimAttemptIdentities()
    {
        Guid deliveryId = Guid.NewGuid();
        Guid attemptId = Guid.NewGuid();
        Guid eventId = Guid.NewGuid();
        const int attemptNumber = 17;
        var deliveryClient = new CapturingDeliveryClient(new DeliveryResult(true, 200));
        var queue = new FakeSubscriptionDeliveryQueue
        {
            ClaimedItems =
            [
                MakeWorkItem(
                    id: deliveryId,
                    attemptId: attemptId,
                    attemptNumber: attemptNumber,
                    eventId: eventId,
                    auth: new ConnectionAuth
                    {
                        Scheme = "api_key_header",
                        Config = JsonSerializer.Deserialize<JsonElement>("""{"header_name":"Integrios-Event-Id"}"""),
                        SecretRefs = ApiKeySecretRefs
                    })
            ]
        };

        IMediator mediator = BuildMediator(services =>
        {
            services.AddSingleton<ISubscriptionDeliveryQueue>(queue);
            services.AddSingleton<IDeliveryClient>(deliveryClient);
            services.AddSingleton<ITransformEvaluator>(new FakeTransformEvaluator());
            services.AddSingleton<IAuthSchemeRegistry>(CreateRegistry());
            services.AddSingleton<ISecretResolver>(new FakeSecretResolver(new Dictionary<string, string> { ["erp_api_key"] = "cannot-overwrite" }));
        });

        await mediator.Send(new DispatchSubscriptionDeliveriesCommand(25));

        Assert.Equal(eventId.ToString(), deliveryClient.Headers["Integrios-Event-Id"]);
        Assert.Equal(deliveryId.ToString(), deliveryClient.Headers["Integrios-Delivery-Id"]);
        Assert.Equal(attemptId.ToString(), deliveryClient.Headers["Integrios-Attempt-Id"]);
        Assert.Equal("17", deliveryClient.Headers["Integrios-Attempt-Number"]);
    }

    [Fact]
    public async Task Dispatch_UnknownAuthScheme_FinalizesRequestConstructionFailure()
    {
        var queue = new FakeSubscriptionDeliveryQueue
        {
            FinalizationResult = Applied(SubscriptionDeliveryDisposition.RetryScheduled),
            ClaimedItems =
            [
                MakeWorkItem(
                    auth: new ConnectionAuth
                    {
                        Scheme = "unsupported",
                        Config = JsonSerializer.Deserialize<JsonElement>("{}"),
                        SecretRefs = JsonSerializer.Deserialize<JsonElement>("{}")
                    })
            ]
        };

        IMediator mediator = BuildMediator(services =>
        {
            services.AddSingleton<ISubscriptionDeliveryQueue>(queue);
            services.AddSingleton<IDeliveryClient>(new CapturingDeliveryClient(new DeliveryResult(true, 200)));
            services.AddSingleton<ITransformEvaluator>(new FakeTransformEvaluator());
            services.AddSingleton<IAuthSchemeRegistry>(CreateRegistry());
            services.AddSingleton<ISecretResolver>(new FakeSecretResolver(new Dictionary<string, string>()));
        });

        await mediator.Send(new DispatchSubscriptionDeliveriesCommand(25));

        DeliveryAttemptCompletion completion = Assert.Single(queue.Completions);
        Assert.False(completion.Succeeded);
        Assert.Equal(DeliveryFailurePhase.RequestConstruction, completion.FailurePhase);
    }

    [Fact]
    public async Task Dispatch_MalformedAuthSnapshot_FinalizesRequestConstructionFailure()
    {
        var queue = new FakeSubscriptionDeliveryQueue
        {
            FinalizationResult = Applied(SubscriptionDeliveryDisposition.RetryScheduled),
            ClaimedItems = [MakeWorkItem(authJson: "[]")]
        };
        var deliveryClient = new CapturingDeliveryClient(new DeliveryResult(true, 200));

        IMediator mediator = BuildMediator(services =>
        {
            services.AddSingleton<ISubscriptionDeliveryQueue>(queue);
            services.AddSingleton<IDeliveryClient>(deliveryClient);
            services.AddSingleton<ITransformEvaluator>(new FakeTransformEvaluator());
            services.AddSingleton<IAuthSchemeRegistry>(CreateRegistry());
            services.AddSingleton<ISecretResolver>(new FakeSecretResolver(new Dictionary<string, string>()));
        });

        await mediator.Send(new DispatchSubscriptionDeliveriesCommand(25));

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
        services.AddIntegriosApplication();
        services.AddSingleton(DeliveryExecutionOptions.Default);
        registerDoubles(services);
        return services.BuildServiceProvider().GetRequiredService<IMediator>();
    }

    private static IAuthSchemeRegistry CreateRegistry() =>
        new AuthSchemeRegistry([new ApiKeyHeaderAuthSchemeHandler(), new BearerTokenAuthSchemeHandler()]);

    private static SubscriptionDeliveryWorkItem MakeWorkItem(
        Guid? id = null,
        Guid? attemptId = null,
        int attemptNumber = 1,
        Guid? eventId = null,
        Guid? subscriptionId = null,
        Guid? destinationConnectionId = null,
        Guid? tenantId = null,
        string integrationKey = "erp_system",
        ConnectionAuth? auth = null,
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
            "https://erp.example/webhook",
            "{\"amount\":42}",
            "payment.created",
            "payments",
            DateTimeOffset.UtcNow,
            null,
            integrationKey,
            authJson ?? (auth is null ? null : JsonSerializer.Serialize(auth)),
            null);

    private sealed class FakeSubscriptionDeliveryQueue : ISubscriptionDeliveryQueue
    {
        public IReadOnlyList<SubscriptionDeliveryWorkItem> ClaimedItems { get; init; } = [];
        public DeliveryFinalizationResult FinalizationResult { get; set; } = Applied(SubscriptionDeliveryDisposition.Succeeded);
        public List<DeliveryAttemptCompletion> Completions { get; } = [];
        public List<DeliveryFinalizationResult> Finalizations { get; } = [];
        private int claimIndex;

        public Task<SubscriptionDeliveryWorkItem?> ClaimNextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<SubscriptionDeliveryWorkItem?>(claimIndex < ClaimedItems.Count ? ClaimedItems[claimIndex++] : null);

        public Task<SubscriptionDeliveryClaimResult?> ClaimNextWithRecoveryAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<SubscriptionDeliveryClaimResult?>(
                claimIndex < ClaimedItems.Count
                    ? new ClaimedSubscriptionDelivery(ClaimedItems[claimIndex++])
                    : null);

        public Task<DeliveryFinalizationResult> FinalizeAsync(DeliveryAttemptCompletion completion, CancellationToken cancellationToken = default)
        {
            Completions.Add(completion);
            Finalizations.Add(FinalizationResult);
            return Task.FromResult(FinalizationResult);
        }

    }

    private static DeliveryFinalizationResult Applied(SubscriptionDeliveryDisposition disposition) =>
        new(DeliveryFinalizationStatus.Applied, disposition);

    // Mirrors framework header validation, which embeds the offending value in its message.
    private sealed class LeakyAuthSchemeHandler : IAuthSchemeHandler
    {
        public string Name => "leaky_scheme";
        public IReadOnlyList<string> RequiredConfigFields => [];
        public IReadOnlyList<string> RequiredSecretFields => ["token"];

        public void Apply(HttpRequestMessage request, JsonElement config, IReadOnlyDictionary<string, string> secrets) =>
            throw new FormatException($"The format of value '{secrets["token"]}' is invalid.");
    }

    private sealed class CapturingDeliveryClient(DeliveryResult result) : IDeliveryClient
    {
        public Dictionary<string, string> Headers { get; } = [];
        public AuthenticationHeaderValue? Authorization { get; private set; }
        public int CallCount { get; private set; }

        public Task<DeliveryResult> DeliverAsync(string url, string payloadJson, Action<HttpRequestMessage>? decorate = null, CancellationToken cancellationToken = default)
        {
            CallCount++;
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            decorate?.Invoke(request);

            foreach (var header in request.Headers)
            {
                Headers[header.Key] = header.Value.Single();
            }

            Authorization = request.Headers.Authorization;
            return Task.FromResult(result);
        }
    }

    private sealed class FakeSecretResolver(IReadOnlyDictionary<string, string> values) : ISecretResolver
    {
        public string ProviderName => "test";
        public List<string> RequestedSecretNames { get; } = [];
        public List<TenantSecretScope> RequestedScopes { get; } = [];

        public Task<string> ResolveAsync(TenantSecretScope tenant, string secretName, CancellationToken cancellationToken = default)
        {
            RequestedScopes.Add(tenant);
            RequestedSecretNames.Add(secretName);

            if (values.TryGetValue(secretName, out string? value))
            {
                return Task.FromResult(value);
            }

            throw new InvalidOperationException($"Secret reference '{secretName}' was not found.");
        }
    }

    private sealed class FakeTransformEvaluator(string? output = null, string? error = null) : ITransformEvaluator
    {
        public string? ValidateExpression(TransformSpec transform) => null;

        public string Evaluate(
            TransformSpec transform,
            string payloadJson,
            TransformContext context)
        {
            _ = transform;
            _ = context;
            if (error is not null)
            {
                throw new TransformEvaluationException(error);
            }

            return output ?? payloadJson;
        }
    }
}
