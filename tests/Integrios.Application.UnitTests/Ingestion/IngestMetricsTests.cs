using System.Text.Json;
using Integrios.Application;
using Integrios.Application.Ingestion;
using Integrios.Application.Telemetry;
using Integrios.Application.Transforms;
using Integrios.Domain.Entities;
using Integrios.Domain.Enums;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Integrios.Tests.Shared;

namespace Integrios.Application.UnitTests;

public sealed class IngestMetricsTests
{
    private static readonly Guid SourceId = Guid.NewGuid();

    [Fact]
    public async Task IngestEventCommand_OnAcceptance_IncrementsEventsIngested()
    {
        using var metrics = new MetricCollector(IntegriosMetrics.MeterName);

        var mediator = BuildMediator(alreadyAccepted: false);
        await mediator.Send(MakeCommand());

        var ingested = metrics.ForInstrument("integrios_events_ingested").ShouldHaveSingleItem();
        ingested.Value.ShouldBe(1);
    }

    [Fact]
    public async Task IngestEventCommand_OnDuplicate_DoesNotIncrementEventsIngested()
    {
        using var metrics = new MetricCollector(IntegriosMetrics.MeterName);

        var mediator = BuildMediator(alreadyAccepted: true);
        await mediator.Send(MakeCommand());

        metrics.ForInstrument("integrios_events_ingested").ShouldBeEmpty();
    }

    [Fact]
    public async Task IngestEventCommand_LogEntries_CarryAcceptanceScopeKeys()
    {
        var capturing = new CapturingLoggerProvider();
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddProvider(capturing));
        services.AddApplicationServices();
        services.AddSingleton<IEventApiSourceResolver>(new FakeEventApiSourceResolver());
        services.AddSingleton<ITransformEvaluator>(new FakeTransformEvaluator());
        services.AddSingleton<IEventAcceptance>(new FakeEventAcceptance(alreadyAccepted: false));
        var mediator = services.BuildServiceProvider().GetRequiredService<IMediator>();

        await mediator.Send(MakeCommand());

        capturing.AnyEntryHasScopeKeys("event_id", "tenant_id", "topic_id").ShouldBeTrue();
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

        result.EventId.ShouldBe(acceptance.EventId);
        result.Status.ShouldBe(acceptance.Status);
        result.AcceptedAt.ShouldBe(acceptedAt);
        result.AlreadyAccepted.ShouldBeTrue();
    }

    [Fact]
    public async Task IngestEventCommand_ConstructsSubmissionForResolvedTopic()
    {
        var topicId = Guid.NewGuid();
        var acceptance = new FakeEventAcceptance(alreadyAccepted: false);
        var command = MakeCommand();
        var mediator = BuildMediator(acceptance, new FakeEventApiSourceResolver(topicId));

        await mediator.Send(command);

        EventSubmission submission = acceptance.LastSubmission.ShouldBeOfType<EventSubmission>();
        submission.TenantId.ShouldBe(command.TenantId);
        submission.TopicId.ShouldBe(topicId);
        submission.SourceId.ShouldBe(command.SourceId);
        submission.EventType.ShouldBe("payment.created");
        submission.Payload.GetProperty("amount").GetInt32().ShouldBe(command.RawInput.GetProperty("amount").GetInt32());
    }

    private static IngestEventCommand MakeCommand() => new(
        Guid.NewGuid(),
        SourceId,
        JsonDocument.Parse("{\"amount\":42}").RootElement);

    private static IMediator BuildMediator(bool alreadyAccepted) => BuildMediator(new EventAcceptance
    {
        EventId = Guid.NewGuid(),
        Status = EventStatus.Accepted,
        AcceptedAt = DateTimeOffset.UtcNow,
        AlreadyAccepted = alreadyAccepted
    });

    private static IMediator BuildMediator(EventAcceptance acceptance) =>
        BuildMediator(new FakeEventAcceptance(acceptance), new FakeEventApiSourceResolver());

    private static IMediator BuildMediator(
        IEventAcceptance eventAcceptance,
        IEventApiSourceResolver sourceResolver)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddApplicationServices();
        services.AddSingleton(sourceResolver);
        services.AddSingleton<ITransformEvaluator>(new FakeTransformEvaluator());
        services.AddSingleton(eventAcceptance);
        return services.BuildServiceProvider().GetRequiredService<IMediator>();
    }

    // Wraps every raw input document as { event_type: "payment.created", payload: <input> },
    // mirroring the built-in http Connector's identity `event_json` contract closely enough for
    // these metrics/wiring tests, without pulling in the real JSONata engine.
    private sealed class FakeTransformEvaluator : ITransformEvaluator
    {
        public string? ValidateExpression(TransformSpec transform) => null;

        public string Evaluate(TransformSpec transform, string payloadJson, TransformContext context) =>
            Wrap(payloadJson);

        public string Evaluate(TransformSpec transform, string payloadJson, JsonElement? context) =>
            Wrap(payloadJson);

        private static string Wrap(string payloadJson) =>
            $$"""{"event_type":"payment.created","payload":{{payloadJson}}}""";
    }

    private sealed class FakeEventApiSourceResolver(Guid? topicId = null) : IEventApiSourceResolver
    {
        public Task<ResolvedEventApiSource?> ResolveAsync(Guid tenantId, Guid sourceId, CancellationToken cancellationToken) =>
            Task.FromResult<ResolvedEventApiSource?>(new ResolvedEventApiSource
            {
                TopicId = topicId ?? Guid.NewGuid(),
                SourceContractSchema = null,
                SourceMapping = new TransformSpec("fake", "1", ""),
            });
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
