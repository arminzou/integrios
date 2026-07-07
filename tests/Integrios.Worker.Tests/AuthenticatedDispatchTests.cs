using System.Net.Http.Headers;
using System.Text.Json;
using Integrios.Application;
using Integrios.Application.Abstractions;
using Integrios.Application.Abstractions.Auth;
using Integrios.Application.Delivery;
using Integrios.Application.Telemetry;
using Integrios.Domain.Integrations;
using Integrios.Infrastructure.Http.Auth;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Integrios.Worker.Tests;

public sealed class AuthenticatedDispatchTests
{
    private static readonly JsonElement EmptyObject = JsonSerializer.Deserialize<JsonElement>("{}");
    private static readonly JsonElement ApiKeyConfig = JsonSerializer.Deserialize<JsonElement>("""{"header_name":"X-Api-Key"}""");
    private static readonly JsonElement ApiKeySecretRefs = JsonSerializer.Deserialize<JsonElement>("""{"api_key":"erp_api_key"}""");

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
        var attempts = new FakeDeliveryAttemptRepository();
        var deliveryClient = new CapturingDeliveryClient(new DeliveryResult(true, 200));
        var secretResolver = new FakeSecretResolver(new Dictionary<string, string> { ["erp_api_key"] = "secret-value" });

        IMediator mediator = BuildMediator(services =>
        {
            services.AddSingleton<ISubscriptionDeliveryQueue>(queue);
            services.AddSingleton<IDeliveryAttemptRepository>(attempts);
            services.AddSingleton<IDeliveryClient>(deliveryClient);
            services.AddSingleton<ITransformEvaluator>(new FakeTransformEvaluator());
            services.AddSingleton<IAuthSchemeRegistry>(CreateRegistry());
            services.AddSingleton<ISecretResolver>(secretResolver);
            services.AddSingleton<ILoggerProvider>(new CapturingLoggerProvider());
        });

        await mediator.Send(new DispatchSubscriptionDeliveriesCommand(25, 3));

        Assert.Equal(["erp_api_key"], secretResolver.RequestedSecretNames);
        Assert.True(deliveryClient.Headers.TryGetValue("X-Api-Key", out string? headerValue));
        Assert.Equal("secret-value", headerValue);
        Assert.Single(queue.SucceededIds);
        Assert.Empty(queue.ScheduledRetries);
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
                    attemptCount: 0,
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
            services.AddSingleton<IDeliveryAttemptRepository>(new FakeDeliveryAttemptRepository());
            services.AddSingleton<IDeliveryClient>(new CapturingDeliveryClient(new DeliveryResult(true, 200)));
            services.AddSingleton<ITransformEvaluator>(new FakeTransformEvaluator());
            services.AddSingleton<IAuthSchemeRegistry>(CreateRegistry());
            services.AddSingleton<ISecretResolver>(new FakeSecretResolver(new Dictionary<string, string>()));
            services.AddSingleton<ILoggerProvider>(new CapturingLoggerProvider());
        });

        await mediator.Send(new DispatchSubscriptionDeliveriesCommand(25, 3));

        Assert.Single(queue.ScheduledRetries);
        Assert.Empty(queue.SucceededIds);
        Assert.Empty(queue.DeadLetteredIds);
    }

    [Fact]
    public async Task Dispatch_MissingSecret_DeadLetters_WhenAttemptsExhausted()
    {
        Guid deliveryId = Guid.NewGuid();
        var queue = new FakeSubscriptionDeliveryQueue
        {
            ClaimedItems =
            [
                MakeWorkItem(
                    id: deliveryId,
                    attemptCount: 2,
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
            services.AddSingleton<IDeliveryAttemptRepository>(new FakeDeliveryAttemptRepository());
            services.AddSingleton<IDeliveryClient>(new CapturingDeliveryClient(new DeliveryResult(true, 200)));
            services.AddSingleton<ITransformEvaluator>(new FakeTransformEvaluator());
            services.AddSingleton<IAuthSchemeRegistry>(CreateRegistry());
            services.AddSingleton<ISecretResolver>(new FakeSecretResolver(new Dictionary<string, string>()));
            services.AddSingleton<ILoggerProvider>(new CapturingLoggerProvider());
        });

        await mediator.Send(new DispatchSubscriptionDeliveriesCommand(25, 3));

        Assert.Single(queue.DeadLetteredIds);
        Assert.Empty(queue.SucceededIds);
    }

    private static IMediator BuildMediator(Action<IServiceCollection> registerDoubles)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMetrics();
        services.AddIntegriosApplication();
        registerDoubles(services);
        return services.BuildServiceProvider().GetRequiredService<IMediator>();
    }

    private static IAuthSchemeRegistry CreateRegistry() =>
        new AuthSchemeRegistry([new ApiKeyHeaderAuthSchemeHandler(), new BearerTokenAuthSchemeHandler()]);

    private static SubscriptionDeliveryWorkItem MakeWorkItem(
        Guid? id = null,
        Guid? eventId = null,
        Guid? subscriptionId = null,
        Guid? destinationConnectionId = null,
        Guid? tenantId = null,
        int attemptCount = 0,
        ConnectionAuth? auth = null)
        => new(
            id ?? Guid.NewGuid(),
            eventId ?? Guid.NewGuid(),
            subscriptionId ?? Guid.NewGuid(),
            destinationConnectionId ?? Guid.NewGuid(),
            tenantId ?? Guid.NewGuid(),
            attemptCount,
            "https://erp.example/webhook",
            "{\"amount\":42}",
            "payment.created",
            "payments",
            DateTimeOffset.UtcNow,
            null,
            "erp_system",
            auth,
            null);

    private sealed class FakeSubscriptionDeliveryQueue : ISubscriptionDeliveryQueue
    {
        public IReadOnlyList<SubscriptionDeliveryWorkItem> ClaimedItems { get; init; } = [];
        public List<Guid> SucceededIds { get; } = [];
        public List<(Guid DeliveryId, int AttemptCount, DateTimeOffset DeliverAfter)> ScheduledRetries { get; } = [];
        public List<Guid> DeadLetteredIds { get; } = [];

        public Task<int> FanoutAsync(Guid eventId, IReadOnlyList<SubscriptionFanoutTarget> targets, string? traceparent = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

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
        public Task<int> GetAttemptCountAsync(Guid eventId, Guid subscriptionId, CancellationToken cancellationToken = default) => Task.FromResult(0);

        public Task RecordAsync(Guid eventId, Guid subscriptionId, Guid destinationConnectionId, int attemptNumber, string status, string requestPayloadJson, int? responseStatusCode, string? responseBody, string? errorMessage, DateTimeOffset startedAt, DateTimeOffset? completedAt, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class CapturingDeliveryClient(DeliveryResult result) : IDeliveryClient
    {
        public Dictionary<string, string> Headers { get; } = [];
        public AuthenticationHeaderValue? Authorization { get; private set; }

        public Task<DeliveryResult> DeliverAsync(string url, string payloadJson, Action<HttpRequestMessage>? decorate = null, CancellationToken cancellationToken = default)
        {
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
