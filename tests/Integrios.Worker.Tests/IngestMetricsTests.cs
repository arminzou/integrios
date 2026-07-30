using System.Text.Json;
using Integrios.Application;
using Integrios.Application.Events;
using Integrios.Application.Telemetry;
using Integrios.Domain.Events;
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
        services.AddApplicationServices();
        services.AddSingleton<ISourceTopicLookup>(new FakeIntakeTopicResolver());
        services.AddSingleton<IEventAcceptance>(new FakeEventAcceptance(isDuplicate: false));
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
        services.AddApplicationServices();
        services.AddSingleton<ISourceTopicLookup>(new FakeIntakeTopicResolver());
        services.AddSingleton<IEventAcceptance>(new FakeEventAcceptance(isDuplicate));
        return services.BuildServiceProvider().GetRequiredService<IMediator>();
    }

    private sealed class FakeIntakeTopicResolver : ISourceTopicLookup
    {
        public Task<Guid?> FindActiveSourceTopicAsync(Guid tenantId, string topicName, Guid sourceConnectionId, CancellationToken cancellationToken = default)
            => Task.FromResult<Guid?>(Guid.NewGuid());
    }

    private sealed class FakeEventAcceptance(bool isDuplicate) : IEventAcceptance
    {
        public Task<IngestEventResponse> AcceptAsync(Guid tenantId, IngestEventRequest request, Guid topicId, string? traceparent = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new IngestEventResponse
            {
                EventId = Guid.NewGuid(),
                Status = EventStatus.Accepted,
                AcceptedAt = DateTimeOffset.UtcNow,
                IsDuplicate = isDuplicate
            });

    }
}
