using System.Text.Json;
using Integrios.Application;
using Integrios.Application.Authoring.Connectors;
using Integrios.Application.Authoring.Destinations;
using Integrios.Application.Authoring.Subscriptions;
using Integrios.Application.Authoring.Topics;
using Integrios.Application.Delivery;
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
    public async Task CreateSubscription_WithoutEventTypes_IsRejectedThroughMediator()
    {
        await using AuthoringHarness harness = new();

        var exception = await Should.ThrowAsync<SubscriptionValidationException>(() =>
            harness.Mediator.Send(harness.Command(eventTypes: [])));

        exception.Field.ShouldBe("event_types");
        harness.SubscriptionRepository.CreateCalls.ShouldBe(0);
    }

    // Only a type a Source on the Topic declares can ever arrive, so a route to anything else is a
    // typo or a guess, and is refused rather than stored.
    [Fact]
    public async Task CreateSubscription_TypeNoSourceDeclares_IsRejectedThroughMediator()
    {
        await using AuthoringHarness harness = new();

        var exception = await Should.ThrowAsync<SubscriptionValidationException>(() =>
            harness.Mediator.Send(harness.Command(eventTypes: ["payment.created", "payment.refunded"])));

        exception.Field.ShouldBe("event_types");
        exception.Message.ShouldContain("payment.refunded", Case.Sensitive);
        harness.SubscriptionRepository.CreateCalls.ShouldBe(0);
    }

    [Fact]
    public async Task CreateSubscription_SelectsSeveralTypesInTheTopicsSpelling()
    {
        await using AuthoringHarness harness = new();

        await harness.Mediator.Send(harness.Command(eventTypes: ["PAYMENT.CREATED", "payment.captured"]));

        harness.SubscriptionRepository.CreatedEventTypes.ShouldBe(["payment.created", "payment.captured"]);
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

        exception.Message.ShouldContain("does not permit Destination authoring", Case.Sensitive);
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
        private readonly Guid destinationId = Guid.NewGuid();
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
            services.AddSingleton<IDestinationRepository>(new FakeDestinationRepository(Destination(connectorId)));
            services.AddSingleton<IDestinationAuthoringLock>(new NoOpDestinationAuthoringLock());
            services.AddSingleton<IConnectorReader>(new FakeConnectorReader(Connector(connectorId, destinationDirection)));
            services.AddSingleton<IDestinationAuthenticatorRegistry>(new EmptyAuthSchemeRegistry());
            services.AddSingleton<ITransformEvaluator>(CreateTransformEvaluator(transformValidationError));
            provider = services.BuildServiceProvider();
            Mediator = provider.GetRequiredService<IMediator>();
        }

        public IMediator Mediator { get; }
        public FakeSubscriptionRepository SubscriptionRepository { get; }

        public CreateSubscriptionCommand Command(
            IReadOnlyList<string>? eventTypes = null,
            JsonElement? transformConfig = null) =>
            new(
                tenantId,
                topicId,
                "destination-subscription",
                eventTypes ?? ["payment.created"],
                destinationId,
                transformConfig,
                HttpDeliveryConfiguration.Default,
                null,
                0,
                null);

        public ValueTask DisposeAsync() => provider.DisposeAsync();

        private Topic Topic() => new()
        {
            Id = topicId,
            TenantId = tenantId,
            Key = "payments",
            Name = "payments",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        private Destination Destination(Guid connectorId) => new()
        {
            Id = destinationId,
            TenantId = tenantId,
            ConnectorId = connectorId,
            Name = "destination",
            Configuration = Json("""{"base_uri":"https://erp.example.test"}"""),
            Status = EnablementStatus.Enabled,
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
        public IReadOnlyCollection<IDestinationAuthenticator> Registered => [];

        public IDestinationAuthenticator GetRequired(string scheme) =>
            throw new InvalidOperationException($"Unexpected scheme '{scheme}'.");

        public bool TryGet(string scheme, out IDestinationAuthenticator handler)
        {
            handler = null!;
            return false;
        }
    }

    private sealed class NoOpDestinationAuthoringLock : IDestinationAuthoringLock
    {
        public Task<IAsyncDisposable> AcquireAsync(
            IEnumerable<Guid> destinationIds,
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

        public Task<Topic> CreateAsync(Guid tenantId, string key, string name, string? description, CancellationToken ct = default) =>
            Task.FromResult(topic);

        public Task<int> CountSubscriptionsAsync(Guid tenantId, Guid topicId, CancellationToken ct = default) =>
            Task.FromResult(0);

        public Task<(IReadOnlyList<TopicListRow> Items, string? NextCursor)> ListByTenantAsync(Guid tenantId, TopicListFilter filter, string? afterCursor, int limit, CancellationToken ct = default) =>
            Task.FromResult<(IReadOnlyList<TopicListRow>, string?)>(([new TopicListRow(topic, 0)], null));

        public Task<Topic?> UpdateAsync(Guid tenantId, Guid id, string? name, string? description, CancellationToken ct = default) =>
            Task.FromResult<Topic?>(topic);

        public Task<IReadOnlyList<SourceDeclaration>> ListSourceDeclarationsAsync(
            Guid tenantId, IReadOnlyCollection<Guid> topicIds, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SourceDeclaration>>(
            [
                new SourceDeclaration(Guid.NewGuid(), topic.Id, ["payment.created"]),
                new SourceDeclaration(Guid.NewGuid(), topic.Id, ["payment.created", "payment.captured"]),
            ]);

        public Task<IReadOnlyList<SubscriptionSelection>> ListSubscriptionSelectionsAsync(
            Guid tenantId, Guid topicId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SubscriptionSelection>>([]);
    }

    private sealed class FakeDestinationRepository(Destination destination) : IDestinationRepository
    {
        public Task<Destination?> GetByIdAsync(Guid tenantId, Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<Destination?>(destination);

        public Task<Destination> CreateAsync(Destination value, CancellationToken cancellationToken = default) =>
            Task.FromResult(value);

        public Task<(IReadOnlyList<DestinationListRow> Items, string? NextCursor)> ListByTenantAsync(Guid tenantId, DestinationListFilter filter, string? afterCursor, int limit, CancellationToken cancellationToken = default) =>
            Task.FromResult<(IReadOnlyList<DestinationListRow>, string?)>(([new DestinationListRow(destination, "http")], null));

        public Task<bool> HasActiveSubscriptionsAsync(Guid tenantId, Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<Destination?> UpdateAsync(Guid tenantId, Guid id, string name, JsonElement configuration, DestinationAuthentication? authentication, string? environment, string? description, CancellationToken cancellationToken = default) =>
            Task.FromResult<Destination?>(destination);

        public Task<bool> SetStatusAsync(Guid tenantId, Guid id, EnablementStatus status, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }

    private sealed class FakeConnectorReader(Connector connector) : IConnectorReader
    {
        public Task<Connector?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<Connector?>(connector);

        public Task<(IReadOnlyList<Connector> Items, string? NextCursor)> ListAsync(ConnectorDirection? direction, string? afterCursor, int limit, CancellationToken cancellationToken = default) =>
            Task.FromResult<(IReadOnlyList<Connector>, string?)>(([connector], null));
    }

    public sealed class FakeSubscriptionRepository : ISubscriptionRepository
    {
        public int CreateCalls { get; private set; }
        public IReadOnlyList<string>? CreatedEventTypes { get; private set; }

        public Task<Subscription?> CreateAsync(
            Guid tenantId,
            Guid topicId,
            string name,
            IReadOnlyList<string> eventTypes,
            Guid destinationId,
            JsonElement? transformConfig,
            HttpDeliveryConfiguration httpDelivery,
            HttpSuccessRule? httpSuccess,
            int orderIndex,
            string? description,
            CancellationToken cancellationToken = default)
        {
            CreateCalls++;
            CreatedEventTypes = eventTypes;
            return Task.FromResult<Subscription?>(null);
        }

        public Task<Subscription?> GetByIdAsync(Guid tenantId, Guid topicId, Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<Subscription?>(null);

        public Task<Subscription?> UpdateAsync(Guid tenantId, Guid topicId, Guid id, string name, IReadOnlyList<string> eventTypes, Guid destinationId, JsonElement? transformConfig, HttpDeliveryConfiguration httpDelivery, HttpSuccessRule? httpSuccess, int orderIndex, string? description, CancellationToken cancellationToken = default) =>
            Task.FromResult<Subscription?>(null);

        public Task<bool> SetStatusAsync(Guid tenantId, Guid topicId, Guid id, EnablementStatus status, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<IReadOnlyList<HttpDeliveryConfiguration>> ListActiveHttpDeliveriesAsync(
            Guid tenantId,
            Guid destinationId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<HttpDeliveryConfiguration>>([]);
    }
}
