using System.Text.Json;
using Integrios.Application;
using Integrios.Application.Abstractions;
using Integrios.Application.Events;
using Integrios.Application.Telemetry;
using Integrios.Domain.Events;
using Integrios.Domain.Topics;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Integrios.Worker.Tests;

public sealed class IngestMetricsTests
{
    private static readonly Guid SourceConnectionId = Guid.NewGuid();

    [Fact]
    public async Task IngestEventCommand_OnAcceptance_IncrementsEventsIngested()
    {
        using var metrics = new MetricCollector(IntegriosMetrics.MeterName);

        var mediator = BuildMediator(isDuplicate: false);
        await mediator.Send(new IngestEventCommand(Guid.NewGuid(), MakeRequest()));

        var ingested = Assert.Single(metrics.ForInstrument("integrios_events_ingested"));
        Assert.Equal(1, ingested.Value);
    }

    [Fact]
    public async Task IngestEventCommand_OnDuplicate_DoesNotIncrementEventsIngested()
    {
        using var metrics = new MetricCollector(IntegriosMetrics.MeterName);

        var mediator = BuildMediator(isDuplicate: true);
        await mediator.Send(new IngestEventCommand(Guid.NewGuid(), MakeRequest()));

        Assert.Empty(metrics.ForInstrument("integrios_events_ingested"));
    }

    [Fact]
    public async Task IngestEventCommand_LogEntries_CarryAcceptanceScopeKeys()
    {
        var capturing = new CapturingLoggerProvider();
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddProvider(capturing));
        services.AddIntegriosApplication();
        services.AddSingleton<ITopicRepository>(new FakeTopicRepository());
        services.AddSingleton<IEventRepository>(new FakeEventRepository(isDuplicate: false));
        var mediator = services.BuildServiceProvider().GetRequiredService<IMediator>();

        await mediator.Send(new IngestEventCommand(Guid.NewGuid(), MakeRequest()));

        Assert.True(capturing.AnyEntryHasScopeKeys("event_id", "tenant_id", "topic_id"));
    }

    private static IngestEventRequest MakeRequest() => new()
    {
        EventType = "payment.created",
        Payload = JsonDocument.Parse("{\"amount\":42}").RootElement,
        TopicName = "payments",
        SourceConnectionId = SourceConnectionId
    };

    private static IMediator BuildMediator(bool isDuplicate)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIntegriosApplication();
        services.AddSingleton<ITopicRepository>(new FakeTopicRepository());
        services.AddSingleton<IEventRepository>(new FakeEventRepository(isDuplicate));
        return services.BuildServiceProvider().GetRequiredService<IMediator>();
    }

    private sealed class FakeTopicRepository : ITopicRepository
    {
        public Task<Guid?> FindByNameAsync(Guid tenantId, string name, CancellationToken ct = default)
            => Task.FromResult<Guid?>(Guid.NewGuid());

        public Task<Topic> CreateAsync(Guid tenantId, string name, string? description, IReadOnlyList<Guid> sourceConnectionIds, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<Topic?> GetByIdAsync(Guid tenantId, Guid id, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<(IReadOnlyList<Topic> Items, string? NextCursor)> ListByTenantAsync(Guid tenantId, string? afterCursor, int limit, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<Topic?> UpdateAsync(Guid tenantId, Guid id, string? description, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<bool> DeactivateAsync(Guid tenantId, Guid id, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<bool> SetSourceConnectionsAsync(Guid tenantId, Guid id, IReadOnlyList<Guid> sourceConnectionIds, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<Guid?> FindActiveSourceTopicAsync(Guid tenantId, string name, Guid sourceConnectionId, CancellationToken ct = default)
            => Task.FromResult<Guid?>(Guid.NewGuid());
    }

    private sealed class FakeEventRepository(bool isDuplicate) : IEventRepository
    {
        public Task<IngestEventResponse> IngestAsync(Guid tenantId, IngestEventRequest request, Guid topicId, string? traceparent = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new IngestEventResponse
            {
                EventId = Guid.NewGuid(),
                Status = EventStatus.Accepted,
                AcceptedAt = DateTimeOffset.UtcNow,
                IsDuplicate = isDuplicate
            });

        public Task<GetEventResponse?> GetEventByIdAsync(Guid tenantId, Guid eventId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<bool> ReplayEventAsync(Guid tenantId, Guid eventId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
