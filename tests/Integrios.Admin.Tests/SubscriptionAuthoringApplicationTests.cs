using System.Text.Json;
using Integrios.Application;
using Integrios.Application.Abstractions;
using Integrios.Application.Subscriptions;
using Integrios.Domain.Common;
using Integrios.Domain.Integrations;
using Integrios.Domain.Topics;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace Integrios.Admin.Tests;

public sealed class SubscriptionAuthoringApplicationTests
{
    [Fact]
    public async Task CreateSubscription_InvalidMatchRules_AreRejectedThroughMediator()
    {
        await using AuthoringHarness harness = new();

        var exception = await Assert.ThrowsAsync<SubscriptionRequestValidationException>(() =>
            harness.Mediator.Send(harness.Command(matchRules: Json("{}"))));

        Assert.Contains("matchRules", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, harness.SubscriptionRepository.CreateCalls);
    }

    [Fact]
    public async Task CreateSubscription_InvalidTransformConfig_IsRejectedThroughMediator()
    {
        await using AuthoringHarness harness = new(transformValidationError: "invalid transform expression");

        var exception = await Assert.ThrowsAsync<SubscriptionRequestValidationException>(() =>
            harness.Mediator.Send(harness.Command(transformConfig: ValidTransform())));

        Assert.Equal("invalid transform expression", exception.Message);
        Assert.Equal(0, harness.SubscriptionRepository.CreateCalls);
    }

    [Fact]
    public async Task CreateSubscription_SourceOnlyDestination_IsRejectedThroughMediator()
    {
        await using AuthoringHarness harness = new(destinationDirection: IntegrationDirection.Source);

        var exception = await Assert.ThrowsAsync<SubscriptionRequestValidationException>(() =>
            harness.Mediator.Send(harness.Command()));

        Assert.Contains("direction is destination or both", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, harness.SubscriptionRepository.CreateCalls);
    }

    [Fact]
    public void TransformConfigValidator_InvalidConfig_ReturnsErrorWithoutThrowing()
    {
        var evaluator = new FakeTransformEvaluator("invalid transform expression");

        string? error = TransformConfigValidator.Validate(ValidTransform(), evaluator, out string expression);

        Assert.Equal("invalid transform expression", error);
        Assert.Equal("amount", expression);
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
            services.AddIntegriosApplication();
            services.AddSingleton<ISubscriptionRepository>(SubscriptionRepository);
            services.AddSingleton<ITopicRepository>(new FakeTopicRepository(Topic()));
            services.AddSingleton<IConnectionRepository>(new FakeConnectionRepository(Connection(integrationId)));
            services.AddSingleton<IIntegrationRepository>(new FakeIntegrationRepository(Integration(integrationId, destinationDirection)));
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
            SourceConnectionIds = [],
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
            Name = "Test Integration",
            Direction = direction,
            SupportedAuthSchemes = [],
            Status = OperationalStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
    }

    private sealed class FakeTransformEvaluator(string? validationError) : ITransformEvaluator
    {
        public string? ValidateExpression(string engine, string version, string expression) => validationError;

        public string Evaluate(string expression, string payloadJson, TransformContext context) => payloadJson;
    }

    private sealed class FakeTopicRepository(Topic topic) : ITopicRepository
    {
        public Task<Topic?> GetByIdAsync(Guid tenantId, Guid id, CancellationToken ct = default) =>
            Task.FromResult<Topic?>(topic);

        public Task<Topic> CreateAsync(Guid tenantId, string name, string? description, IReadOnlyList<Guid> sourceConnectionIds, CancellationToken ct = default) =>
            Task.FromResult(topic);

        public Task<(IReadOnlyList<Topic> Items, string? NextCursor)> ListByTenantAsync(Guid tenantId, string? afterCursor, int limit, CancellationToken ct = default) =>
            Task.FromResult<(IReadOnlyList<Topic>, string?)>(([topic], null));

        public Task<Topic?> UpdateAsync(Guid tenantId, Guid id, string? description, IReadOnlyList<Guid>? sourceConnectionIds, CancellationToken ct = default) =>
            Task.FromResult<Topic?>(topic);

        public Task<bool> DeactivateAsync(Guid tenantId, Guid id, CancellationToken ct = default) =>
            Task.FromResult(true);

        public Task<Guid?> FindByNameAsync(Guid tenantId, string name, CancellationToken ct = default) =>
            Task.FromResult<Guid?>(topic.Id);

        public Task<Guid?> FindActiveSourceTopicAsync(Guid tenantId, string name, Guid sourceConnectionId, CancellationToken ct = default) =>
            Task.FromResult<Guid?>(topic.Id);
    }

    private sealed class FakeConnectionRepository(Connection connection) : IConnectionRepository
    {
        public Task<Connection?> GetByIdAsync(Guid tenantId, Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<Connection?>(connection);

        public Task<Connection> CreateAsync(Connection value, CancellationToken cancellationToken = default) =>
            Task.FromResult(value);

        public Task<(IReadOnlyList<Connection> Items, string? NextCursor)> ListByTenantAsync(Guid tenantId, string? afterCursor, int limit, CancellationToken cancellationToken = default) =>
            Task.FromResult<(IReadOnlyList<Connection>, string?)>(([connection], null));

        public Task<Connection?> UpdateAsync(Guid tenantId, Guid id, string name, JsonElement config, ConnectionAuth? auth, string? environment, string? description, CancellationToken cancellationToken = default) =>
            Task.FromResult<Connection?>(connection);

        public Task<bool> DeactivateAsync(Guid tenantId, Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }

    private sealed class FakeIntegrationRepository(Integration integration) : IIntegrationRepository
    {
        public Task<Integration?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<Integration?>(integration);

        public Task<(IReadOnlyList<Integration> Items, string? NextCursor)> ListAsync(string? afterCursor, int limit, CancellationToken cancellationToken = default) =>
            Task.FromResult<(IReadOnlyList<Integration>, string?)>(([integration], null));

        public Task<Integration> UpsertBuiltinAsync(Integration value, CancellationToken cancellationToken = default) =>
            Task.FromResult(value);
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
