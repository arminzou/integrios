using System.Text.Json;
using Integrios.Application;
using Integrios.Application.Auth;
using Integrios.Application.Transforms;
using Integrios.Application.Connections;
using Integrios.Application.Integrations;
using Integrios.Application.Subscriptions;
using Integrios.Application.Topics;
using Integrios.Domain.Common;
using Integrios.Domain.Integrations;
using Integrios.Domain.Topics;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace Integrios.Application.UnitTests;

public sealed class SubscriptionAuthoringApplicationTests
{
    [Fact]
    public async Task CreateSubscription_InvalidMatchRules_AreRejectedThroughMediator()
    {
        await using AuthoringHarness harness = new();

        var exception = await Assert.ThrowsAsync<SubscriptionValidationException>(() =>
            harness.Mediator.Send(harness.Command(matchRules: Json("{}"))));

        Assert.Contains("matchRules", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, harness.SubscriptionRepository.CreateCalls);
    }

    [Fact]
    public async Task CreateSubscription_InvalidTransformConfig_IsRejectedThroughMediator()
    {
        await using AuthoringHarness harness = new(transformValidationError: "invalid transform expression");

        var exception = await Assert.ThrowsAsync<SubscriptionValidationException>(() =>
            harness.Mediator.Send(harness.Command(transformConfig: ValidTransform())));

        Assert.Equal("invalid transform expression", exception.Message);
        Assert.Equal(0, harness.SubscriptionRepository.CreateCalls);
    }

    [Fact]
    public async Task CreateSubscription_SourceOnlyDestination_IsRejectedThroughMediator()
    {
        await using AuthoringHarness harness = new(destinationDirection: IntegrationDirection.Source);

        var exception = await Assert.ThrowsAsync<SubscriptionValidationException>(() =>
            harness.Mediator.Send(harness.Command()));

        Assert.Contains("direction permits destination use", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, harness.SubscriptionRepository.CreateCalls);
    }

    private static JsonElement Json(string value) =>
        JsonDocument.Parse(value).RootElement.Clone();

    private static JsonElement ValidTransform() =>
        Json("""{"engine":"jsonata","version":"1","expression":"amount"}""");

    private sealed class AuthoringHarness : IAsyncDisposable
    {
        private readonly Guid tenantId = Guid.NewGuid();
        private readonly Guid topicId = Guid.NewGuid();
        private readonly Guid connectionId = Guid.NewGuid();
        private readonly ServiceProvider provider;

        public AuthoringHarness(
            IntegrationDirection destinationDirection = IntegrationDirection.Both,
            string? transformValidationError = null)
        {
            SubscriptionRepository = new FakeSubscriptionRepository();
            var integrationId = Guid.NewGuid();
            var services = new ServiceCollection();
            services.AddApplicationServices();
            services.AddSingleton<ISubscriptionRepository>(SubscriptionRepository);
            services.AddSingleton<ITopicRepository>(new FakeTopicRepository(Topic()));
            services.AddSingleton<IConnectionRepository>(new FakeConnectionRepository(Connection(integrationId)));
            services.AddSingleton<IConnectionAuthoringLock>(new NoOpConnectionAuthoringLock());
            services.AddSingleton<IIntegrationCatalog>(new FakeIntegrationCatalog(Integration(integrationId, destinationDirection)));
            services.AddSingleton<IAuthSchemeRegistry>(new EmptyAuthSchemeRegistry());
            services.AddSingleton<ITransformEvaluator>(new FakeTransformEvaluator(transformValidationError));
            provider = services.BuildServiceProvider();
            Mediator = provider.GetRequiredService<IMediator>();
        }

        public IMediator Mediator { get; }
        public FakeSubscriptionRepository SubscriptionRepository { get; }

        public CreateSubscriptionCommand Command(
            JsonElement? matchRules = null,
            JsonElement? transformConfig = null) =>
            new(
                tenantId,
                topicId,
                "destination-subscription",
                matchRules ?? Json("""{"event_type":"payment.created"}"""),
                connectionId,
                transformConfig,
                0,
                null);

        public ValueTask DisposeAsync() => provider.DisposeAsync();

        private Topic Topic() => new()
        {
            Id = topicId,
            TenantId = tenantId,
            Name = "payments",
            Sources = [],
            Status = OperationalStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        private Connection Connection(Guid integrationId) => new()
        {
            Id = connectionId,
            TenantId = tenantId,
            IntegrationId = integrationId,
            Name = "destination",
            Config = Json("{}"),
            Status = OperationalStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        private static Integration Integration(Guid integrationId, IntegrationDirection direction) => new()
        {
            Id = integrationId,
            Key = "test_integration",
            ContractVersion = 1,
            ManifestSchemaVersion = 1,
            Name = "Test Integration",
            Direction = direction,
            SupportedAuthSchemes = [],
            Status = OperationalStatus.Active,
            Manifest = new IntegrationManifest
            {
                ManifestSchemaVersion = 1,
                Key = "test_integration",
                ContractVersion = 1,
                Direction = direction.ToString().ToLowerInvariant(),
                SourceConfigurationSchema = direction is IntegrationDirection.Source or IntegrationDirection.Both
                    ? Json("""{"type":"object","properties":{},"additionalProperties":true}""")
                    : null,
                DestinationConfigurationSchema = direction is IntegrationDirection.Destination or IntegrationDirection.Both
                    ? Json("""{"type":"object","properties":{},"additionalProperties":true}""")
                    : null,
                SourceVerification = new IntegrationSourceVerificationManifest { AllowUnverified = true },
                DestinationAuthentication = new IntegrationDestinationAuthenticationManifest { AllowUnauthenticated = true },
                Presentation = new IntegrationPresentationManifest { Name = "Test Integration" },
            },
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
    }

    private sealed class FakeTransformEvaluator(string? validationError) : ITransformEvaluator
    {
        public string? ValidateExpression(TransformSpec transform) => validationError;

        public string Evaluate(
            TransformSpec transform,
            string payloadJson,
            TransformContext context) => payloadJson;
    }

    private sealed class EmptyAuthSchemeRegistry : IAuthSchemeRegistry
    {
        public IAuthSchemeHandler GetRequired(string scheme) =>
            throw new InvalidOperationException($"Unexpected scheme '{scheme}'.");

        public bool TryGet(string scheme, out IAuthSchemeHandler handler)
        {
            handler = null!;
            return false;
        }
    }

    private sealed class NoOpConnectionAuthoringLock : IConnectionAuthoringLock
    {
        public Task<IAsyncDisposable> AcquireAsync(
            IEnumerable<Guid> connectionIds,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IAsyncDisposable>(new NoOpLease());

        private sealed class NoOpLease : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class FakeTopicRepository(Topic topic) : ITopicRepository
    {
        public Task<Topic?> GetByIdAsync(Guid tenantId, Guid id, CancellationToken ct = default) =>
            Task.FromResult<Topic?>(topic);

        public Task<Topic> CreateAsync(Guid tenantId, string name, string? description, IReadOnlyList<Guid> sourceConnectionIds, CancellationToken ct = default) =>
            Task.FromResult(topic);

        public Task<(IReadOnlyList<Topic> Items, string? NextCursor)> ListByTenantAsync(Guid tenantId, string? afterCursor, int limit, CancellationToken ct = default) =>
            Task.FromResult<(IReadOnlyList<Topic>, string?)>(([topic], null));

        public Task<Topic?> UpdateAsync(Guid tenantId, Guid id, string? name, string? description, IReadOnlyList<Guid>? sourceConnectionIds, CancellationToken ct = default) =>
            Task.FromResult<Topic?>(topic);

        public Task<bool> DeactivateAsync(Guid tenantId, Guid id, CancellationToken ct = default) =>
            Task.FromResult(true);

    }

    private sealed class FakeConnectionRepository(Connection connection) : IConnectionRepository
    {
        public Task<Connection?> GetByIdAsync(Guid tenantId, Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<Connection?>(connection);

        public Task<Connection> CreateAsync(Connection value, CancellationToken cancellationToken = default) =>
            Task.FromResult(value);

        public Task<(IReadOnlyList<Connection> Items, string? NextCursor)> ListByTenantAsync(Guid tenantId, string? afterCursor, int limit, CancellationToken cancellationToken = default) =>
            Task.FromResult<(IReadOnlyList<Connection>, string?)>(([connection], null));

        public Task<ConnectionUsage> GetUsageAsync(Guid tenantId, Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ConnectionUsage(false, false));

        public Task<Connection?> UpdateAsync(Guid tenantId, Guid id, string name, JsonElement config, ConnectionSchemeSelection? sourceVerification, ConnectionSchemeSelection? destinationAuthentication, string? environment, string? description, CancellationToken cancellationToken = default) =>
            Task.FromResult<Connection?>(connection);

        public Task<bool> DeactivateAsync(Guid tenantId, Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }

    private sealed class FakeIntegrationCatalog(Integration integration) : IIntegrationCatalog
    {
        public Task<Integration?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<Integration?>(integration);

        public Task<(IReadOnlyList<Integration> Items, string? NextCursor)> ListAsync(string? afterCursor, int limit, CancellationToken cancellationToken = default) =>
            Task.FromResult<(IReadOnlyList<Integration>, string?)>(([integration], null));
    }

    public sealed class FakeSubscriptionRepository : ISubscriptionRepository
    {
        public int CreateCalls { get; private set; }

        public Task<Subscription?> CreateAsync(
            Guid tenantId,
            Guid topicId,
            string name,
            JsonElement matchRules,
            Guid destinationConnectionId,
            JsonElement? transformConfig,
            int orderIndex,
            string? description,
            CancellationToken cancellationToken = default)
        {
            CreateCalls++;
            return Task.FromResult<Subscription?>(null);
        }

        public Task<Subscription?> GetByIdAsync(Guid tenantId, Guid topicId, Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<Subscription?>(null);

        public Task<(IReadOnlyList<Subscription> Items, string? NextCursor)> ListByTopicAsync(Guid tenantId, Guid topicId, string? afterCursor, int limit, CancellationToken cancellationToken = default) =>
            Task.FromResult<(IReadOnlyList<Subscription>, string?)>(([], null));

        public Task<Subscription?> UpdateAsync(Guid tenantId, Guid topicId, Guid id, string name, JsonElement matchRules, Guid destinationConnectionId, JsonElement? transformConfig, int orderIndex, string? description, CancellationToken cancellationToken = default) =>
            Task.FromResult<Subscription?>(null);

        public Task<bool> DeactivateAsync(Guid tenantId, Guid topicId, Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
    }
}
