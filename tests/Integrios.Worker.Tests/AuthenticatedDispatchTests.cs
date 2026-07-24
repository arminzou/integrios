using System.Net.Http.Headers;
using System.Text.Json;
using Integrios.Application;
using Integrios.Application.Abstractions;
using Integrios.Application.Abstractions.Auth;
using Integrios.Application.Delivery;
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
    public async Task Dispatch_MissingSecret_SchedulesRetry()
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
        queue.FinalizationResult = Applied(SubscriptionDeliveryDisposition.RetryScheduled);

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

        DeliveryAttemptCompletion completion = Assert.Single(queue.Completions);
        Assert.False(completion.Succeeded);
        Assert.Equal(DeliveryFailurePhase.SecretResolution, completion.FailurePhase);
        Assert.Equal(SubscriptionDeliveryDisposition.RetryScheduled, Assert.Single(queue.Finalizations).Disposition);
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
            "https://erp.example/webhook",
            "{\"amount\":42}",
            "payment.created",
            "payments",
            DateTimeOffset.UtcNow,
            null,
            "erp_system",
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

        public Task<DeliveryFinalizationResult> FinalizeAsync(DeliveryAttemptCompletion completion, CancellationToken cancellationToken = default)
        {
            Completions.Add(completion);
            Finalizations.Add(FinalizationResult);
            return Task.FromResult(FinalizationResult);
        }

        public Task<bool> ReplayDeadLetteredAsync(Guid tenantId, Guid eventId, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
    }

    private static DeliveryFinalizationResult Applied(SubscriptionDeliveryDisposition disposition) =>
        new(DeliveryFinalizationStatus.Applied, disposition);

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
        public List<string> RequestedSecretNames { get; } = [];

        public Task<string> ResolveAsync(Guid tenantId, string secretName, CancellationToken cancellationToken = default)
        {
            _ = tenantId;
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
        public string? ValidateExpression(string engine, string version, string expression) => null;

        public string Evaluate(string expression, string payloadJson, TransformContext context)
        {
            _ = expression;
            _ = context;
            if (error is not null)
            {
                throw new TransformEvaluationException(error);
            }

            return output ?? payloadJson;
        }
    }
}
