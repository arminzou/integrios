using System.Text.Json;
using Integrios.Application;
using Integrios.Application.Delivery;
using Integrios.Application.Authoring.Connections;
using Integrios.Application.Authoring.Connectors;
using Integrios.Application.Authoring.Subscriptions;
using Integrios.Application.Authoring.Topics;
using Integrios.Application.Transforms;
using Integrios.Domain.Entities;
using Integrios.Domain.Enums;
using Integrios.Domain.ValueObjects;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Integrios.Application.UnitTests;

public sealed class SubscriptionAuthoringApplicationTests
{
    [Fact]
    public async Task CreateSubscription_InvalidMatchRules_AreRejectedThroughMediator()
    {
        await using AuthoringHarness harness = new();

        var exception = await Should.ThrowAsync<SubscriptionValidationException>(() =>
            harness.Mediator.Send(harness.Command(matchRules: Json("{}"))));

        exception.Message.ShouldContain("matchRules", Case.Sensitive);
        harness.SubscriptionRepository.CreateCalls.ShouldBe(0);
    }

    [Fact]
    public async Task CreateSubscription_InvalidMappingConfig_IsRejectedThroughMediator()
    {
        await using AuthoringHarness harness = new(transformValidationError: "invalid transform expression");

        var exception = await Should.ThrowAsync<SubscriptionValidationException>(() =>
            harness.Mediator.Send(harness.Command(transformConfig: ValidTransform())));

        exception.Message.ShouldBe("invalid transform expression");
        harness.SubscriptionRepository.CreateCalls.ShouldBe(0);
    }

    [Fact]
    public async Task CreateSubscription_SourceOnlyDestination_IsRejectedThroughMediator()
    {
        await using AuthoringHarness harness = new(destinationDirection: ConnectorDirection.Source);

        var exception = await Should.ThrowAsync<SubscriptionValidationException>(() =>
            harness.Mediator.Send(harness.Command()));

        exception.Message.ShouldContain("direction permits destination use", Case.Sensitive);
        harness.SubscriptionRepository.CreateCalls.ShouldBe(0);
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
            ConnectorDirection destinationDirection = ConnectorDirection.Both,
            string? transformValidationError = null)
        {
            SubscriptionRepository = new FakeSubscriptionRepository();
            var connectorId = Guid.NewGuid();
            var services = new ServiceCollection();
            services.AddApplicationServices();
            services.AddSingleton<ISubscriptionRepository>(SubscriptionRepository);
            services.AddSingleton<ITopicRepository>(new FakeTopicRepository(Topic()));
            services.AddSingleton<IConnectionRepository>(new FakeConnectionRepository(Connection(connectorId)));
            services.AddSingleton<IConnectionAuthoringLock>(new NoOpConnectionAuthoringLock());
            services.AddSingleton<IConnectorCatalog>(new FakeConnectorCatalog(Connector(connectorId, destinationDirection)));
            services.AddSingleton<IDestinationAuthenticatorRegistry>(new EmptyAuthSchemeRegistry());
            services.AddSingleton<ITransformEvaluator>(CreateTransformEvaluator(transformValidationError));
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
                HttpDeliveryConfiguration.Default,
                0,
                null);

        public ValueTask DisposeAsync() => provider.DisposeAsync();

        private Topic Topic() => new()
        {
            Id = topicId,
            TenantId = tenantId,
            Name = "payments",
            Status = OperationalStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        private Connection Connection(Guid connectorId) => new()
        {
            Id = connectionId,
            TenantId = tenantId,
            ConnectorId = connectorId,
            Name = "destination",
            Config = Json("{}"),
            Status = OperationalStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        private static Connector Connector(Guid connectorId, ConnectorDirection direction) => new()
        {
            Id = connectorId,
            Key = "test_connector",
            ContractVersion = 1,
            ManifestSchemaVersion = 1,
            Name = "Test Connector",
            Direction = direction,
            Status = OperationalStatus.Active,
            Manifest = new ConnectorManifest
            {
                ManifestSchemaVersion = 1,
                Key = "test_connector",
                ContractVersion = 1,
                Direction = direction.ToString().ToLowerInvariant(),
                SourceConfigurationSchema = direction is ConnectorDirection.Source or ConnectorDirection.Both
                    ? Json("""{"type":"object","properties":{},"additionalProperties":true}""")
                    : null,
                DestinationConfigurationSchema = direction is ConnectorDirection.Destination or ConnectorDirection.Both
                    ? Json("""{"type":"object","properties":{},"additionalProperties":true}""")
                    : null,
                SourceVerification = new ConnectorSourceVerificationManifest { AllowUnverified = true },
                DestinationAuthentication = new ConnectorDestinationAuthenticationManifest { AllowUnauthenticated = true },
                Presentation = new ConnectorPresentationManifest { Name = "Test Connector" },
            },
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
    }

    private static ITransformEvaluator CreateTransformEvaluator(string? validationError)
    {
        var evaluator = Substitute.For<ITransformEvaluator>();
        evaluator.ValidateExpression(Arg.Any<TransformSpec>()).Returns(validationError);
        evaluator.Evaluate(Arg.Any<TransformSpec>(), Arg.Any<string>(), Arg.Any<TransformContext>())
            .Returns(callInfo => callInfo.ArgAt<string>(1));
        return evaluator;
    }

    private sealed class EmptyAuthSchemeRegistry : IDestinationAuthenticatorRegistry
    {
        public IDestinationAuthenticator GetRequired(string scheme) =>
            throw new InvalidOperationException($"Unexpected scheme '{scheme}'.");

        public bool TryGet(string scheme, out IDestinationAuthenticator handler)
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

        public Task<Topic> CreateAsync(Guid tenantId, string name, string? description, CancellationToken ct = default) =>
            Task.FromResult(topic);

        public Task<(IReadOnlyList<Topic> Items, string? NextCursor)> ListByTenantAsync(Guid tenantId, string? afterCursor, int limit, CancellationToken ct = default) =>
            Task.FromResult<(IReadOnlyList<Topic>, string?)>(([topic], null));

        public Task<Topic?> UpdateAsync(Guid tenantId, Guid id, string? name, string? description, CancellationToken ct = default) =>
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

        public Task<Connection?> UpdateAsync(Guid tenantId, Guid id, string name, JsonElement config, SourceVerification? sourceVerification, DestinationAuthentication? destinationAuthentication, string? environment, string? description, CancellationToken cancellationToken = default) =>
            Task.FromResult<Connection?>(connection);

        public Task<bool> DeactivateAsync(Guid tenantId, Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }

    private sealed class FakeConnectorCatalog(Connector connector) : IConnectorCatalog
    {
        public Task<Connector?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<Connector?>(connector);

        public Task<(IReadOnlyList<Connector> Items, string? NextCursor)> ListAsync(string? afterCursor, int limit, CancellationToken cancellationToken = default) =>
            Task.FromResult<(IReadOnlyList<Connector>, string?)>(([connector], null));
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
            HttpDeliveryConfiguration httpDelivery,
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

        public Task<Subscription?> UpdateAsync(Guid tenantId, Guid topicId, Guid id, string name, JsonElement matchRules, Guid destinationConnectionId, JsonElement? transformConfig, HttpDeliveryConfiguration httpDelivery, int orderIndex, string? description, CancellationToken cancellationToken = default) =>
            Task.FromResult<Subscription?>(null);

        public Task<bool> DeactivateAsync(Guid tenantId, Guid topicId, Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<IReadOnlyList<HttpDeliveryConfiguration>> ListActiveHttpDeliveriesAsync(
            Guid tenantId,
            Guid destinationConnectionId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<HttpDeliveryConfiguration>>([]);
    }
}
