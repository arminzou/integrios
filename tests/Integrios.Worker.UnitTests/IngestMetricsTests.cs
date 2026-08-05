using System.Text.Json;
using Integrios.Application;
using Integrios.Application.Events;
using Integrios.Application.Telemetry;
using Integrios.Domain.Events;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Integrios.Worker.UnitTests;

public sealed class IngestMetricsTests
{
    private static readonly Guid SourceConnectionId = Guid.NewGuid();

    [Fact]
    public async Task IngestEventCommand_OnAcceptance_IncrementsEventsIngested()
    {
        using var metrics = new MetricCollector(IntegriosMetrics.MeterName);

        var mediator = BuildMediator(alreadyAccepted: false);
        await mediator.Send(MakeCommand());

        var ingested = Assert.Single(metrics.ForInstrument("integrios_events_ingested"));
        Assert.Equal(1, ingested.Value);
    }

    [Fact]
    public async Task IngestEventCommand_OnDuplicate_DoesNotIncrementEventsIngested()
    {
        using var metrics = new MetricCollector(IntegriosMetrics.MeterName);

        var mediator = BuildMediator(alreadyAccepted: true);
        await mediator.Send(MakeCommand());

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
        services.AddSingleton<IEventAcceptance>(new FakeEventAcceptance(alreadyAccepted: false));
        var mediator = services.BuildServiceProvider().GetRequiredService<IMediator>();

        await mediator.Send(MakeCommand());

        Assert.True(capturing.AnyEntryHasScopeKeys("event_id", "tenant_id", "topic_id"));
    }

    [Fact]
    public async Task IngestEventCommand_MapsEventAcceptanceToUseCaseResult()
    {
        var acceptedAt = DateTimeOffset.UtcNow;
        var acceptance = new EventAcceptance
        {
            EventId = Guid.NewGuid(),
            Status = EventStatus.Accepted,
            AcceptedAt = acceptedAt,
            AlreadyAccepted = true
        };
        var mediator = BuildMediator(acceptance);

        IngestEventResult result = await mediator.Send(MakeCommand());

        Assert.Equal(acceptance.EventId, result.EventId);
        Assert.Equal(acceptance.Status, result.Status);
        Assert.Equal(acceptedAt, result.AcceptedAt);
        Assert.True(result.AlreadyAccepted);
    }

    [Fact]
    public async Task IngestEventCommand_ConstructsSubmissionForResolvedTopic()
    {
        var topicId = Guid.NewGuid();
        var acceptance = new FakeEventAcceptance(alreadyAccepted: false);
        var command = MakeCommand();
        var mediator = BuildMediator(acceptance, new FakeIntakeTopicResolver(topicId));

        await mediator.Send(command);

        EventSubmission submission = Assert.IsType<EventSubmission>(acceptance.LastSubmission);
        Assert.Equal(command.TenantId, submission.TenantId);
        Assert.Equal(topicId, submission.TopicId);
        Assert.Equal(command.SourceConnectionId, submission.SourceConnectionId);
        Assert.Equal(command.SourceEventId, submission.SourceEventId);
        Assert.Equal(command.EventType, submission.EventType);
        Assert.Equal(command.Payload.GetRawText(), submission.Payload.GetRawText());
        Assert.Equal(command.Metadata, submission.Metadata);
        Assert.Equal(command.IdempotencyKey, submission.IdempotencyKey);
    }

    private static IngestEventCommand MakeCommand() => new(
        Guid.NewGuid(),
        SourceConnectionId,
        "payments",
        SourceEventId: null,
        "payment.created",
        JsonDocument.Parse("{\"amount\":42}").RootElement,
        Metadata: null,
        IdempotencyKey: null);

    private static IMediator BuildMediator(bool alreadyAccepted) => BuildMediator(new EventAcceptance
    {
        EventId = Guid.NewGuid(),
        Status = EventStatus.Accepted,
        AcceptedAt = DateTimeOffset.UtcNow,
        AlreadyAccepted = alreadyAccepted
    });

    private static IMediator BuildMediator(EventAcceptance acceptance) =>
        BuildMediator(new FakeEventAcceptance(acceptance), new FakeIntakeTopicResolver());

    private static IMediator BuildMediator(
        IEventAcceptance eventAcceptance,
        ISourceTopicLookup topicResolver)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddApplicationServices();
        services.AddSingleton(topicResolver);
        services.AddSingleton(eventAcceptance);
        return services.BuildServiceProvider().GetRequiredService<IMediator>();
    }

    private sealed class FakeIntakeTopicResolver(Guid? topicId = null) : ISourceTopicLookup
    {
        public Task<Guid?> FindActiveSourceTopicAsync(Guid tenantId, string topicName, Guid sourceConnectionId, CancellationToken cancellationToken)
            => Task.FromResult<Guid?>(topicId ?? Guid.NewGuid());
    }

    private sealed class FakeEventAcceptance : IEventAcceptance
    {
        private readonly EventAcceptance acceptance;
        public EventSubmission? LastSubmission { get; private set; }

        public FakeEventAcceptance(bool alreadyAccepted)
            : this(new EventAcceptance
            {
                EventId = Guid.NewGuid(),
                Status = EventStatus.Accepted,
                AcceptedAt = DateTimeOffset.UtcNow,
                AlreadyAccepted = alreadyAccepted
            })
        {
        }

        public FakeEventAcceptance(EventAcceptance acceptance)
        {
            this.acceptance = acceptance;
        }

        public Task<EventAcceptance> AcceptAsync(
            EventSubmission submission,
            string? traceparent,
            CancellationToken cancellationToken)
        {
            LastSubmission = submission;
            return Task.FromResult(acceptance);
        }

    }
}
