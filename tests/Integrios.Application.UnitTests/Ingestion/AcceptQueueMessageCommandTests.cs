using System.Text.Json;
using Integrios.Application.Ingestion;
using Integrios.Application.Transforms;
using Integrios.Domain.Entities;
using Integrios.Domain.Enums;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace Integrios.Application.UnitTests;

public sealed class AcceptQueueMessageCommandTests : IDisposable
{
    private readonly ServiceProvider provider;
    private readonly IMediator mediator;
    private readonly FakeEventAcceptance eventAcceptance = new();

    public AcceptQueueMessageCommandTests()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddApplicationServices();
        services.AddSingleton<ITransformEvaluator>(new FakeTransformEvaluator());
        services.AddSingleton<IEventAcceptance>(eventAcceptance);
        provider = services.BuildServiceProvider();
        mediator = provider.GetRequiredService<IMediator>();
    }

    public void Dispose() => provider.Dispose();

    private static readonly TransformSpec IdentityMapping =
        new("jsonata", "1", "{ \"event_type\": event_type, \"source_event_id\": source_event_id, \"payload\": payload }");

    [Fact]
    public async Task Handle_AcceptsMappedMessage_WithServiceBusPrefixedIdempotencyKey()
    {
        Guid tenantId = Guid.NewGuid(), topicId = Guid.NewGuid(), sourceId = Guid.NewGuid();
        JsonElement input = JsonDocument.Parse(
            """{"event_type":"order.created","source_event_id":"op-1","payload":{"amount":42}}""").RootElement;

        IngestEventResult result = await mediator.Send(
            new AcceptQueueMessageCommand(tenantId, topicId, sourceId, null, IdentityMapping, input));

        result.Status.ShouldBe(EventStatus.Accepted);
        eventAcceptance.LastSubmission.ShouldNotBeNull();
        EventSubmission submission = eventAcceptance.LastSubmission!;
        submission.TenantId.ShouldBe(tenantId);
        submission.TopicId.ShouldBe(topicId);
        submission.SourceId.ShouldBe(sourceId);
        submission.EventType.ShouldBe("order.created");
        submission.SourceEventId.ShouldBe("op-1");
        submission.IdempotencyKey.ShouldBe($"service_bus:{sourceId}:op-1");
    }

    [Fact]
    public async Task Handle_DuplicateIdempotencyKey_ReportsAlreadyAccepted()
    {
        eventAcceptance.AlreadyAccepted = true;
        JsonElement input = JsonDocument.Parse(
            """{"event_type":"order.created","source_event_id":"op-1","payload":{}}""").RootElement;

        IngestEventResult result = await mediator.Send(
            new AcceptQueueMessageCommand(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, IdentityMapping, input));

        result.AlreadyAccepted.ShouldBeTrue();
    }

    [Fact]
    public async Task Handle_SchemaInvalidInput_ThrowsEventAcceptanceException()
    {
        JsonElement schema = JsonDocument.Parse(
            """{"type":"object","properties":{"event_type":{"type":"string"}},"required":["event_type"],"additionalProperties":true}""").RootElement;
        JsonElement input = JsonDocument.Parse("""{"event_type":42,"payload":{}}""").RootElement;

        await Should.ThrowAsync<EventAcceptanceException>(() => mediator.Send(
            new AcceptQueueMessageCommand(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), schema, IdentityMapping, input)));
        eventAcceptance.LastSubmission.ShouldBeNull();
    }

    [Fact]
    public async Task Handle_MappingEvaluationFailure_ThrowsEventAcceptanceException()
    {
        var failingMapping = new TransformSpec("jsonata", "1", "$error(\"boom\")");
        JsonElement input = JsonDocument.Parse("""{"a":1}""").RootElement;

        await Should.ThrowAsync<EventAcceptanceException>(() => mediator.Send(
            new AcceptQueueMessageCommand(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, failingMapping, input)));
        eventAcceptance.LastSubmission.ShouldBeNull();
    }

    [Fact]
    public async Task Handle_PassthroughContractMissingEventType_ThrowsEventAcceptanceException()
    {
        JsonElement input = JsonDocument.Parse("""{"payload":{}}""").RootElement;

        await Should.ThrowAsync<EventAcceptanceException>(() => mediator.Send(
            new AcceptQueueMessageCommand(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, null, input)));
        eventAcceptance.LastSubmission.ShouldBeNull();
    }

    private sealed class FakeEventAcceptance : IEventAcceptance
    {
        public bool AlreadyAccepted { get; set; }
        public EventSubmission? LastSubmission { get; private set; }

        public Task<EventAcceptance> AcceptAsync(EventSubmission submission, string? traceparent, CancellationToken cancellationToken)
        {
            LastSubmission = submission;
            return Task.FromResult(new EventAcceptance
            {
                EventId = Guid.NewGuid(),
                Status = EventStatus.Accepted,
                AcceptedAt = DateTimeOffset.UtcNow,
                AlreadyAccepted = AlreadyAccepted,
            });
        }
    }

    // Passes the input through unchanged for identity mappings and fails when the expression is an
    // explicit $error — enough for the handler's mapping-failure path without the real JSONata engine.
    private sealed class FakeTransformEvaluator : ITransformEvaluator
    {
        public string? ValidateExpression(TransformSpec transform) => null;

        public string Evaluate(TransformSpec transform, string payloadJson, TransformContext context)
            => Evaluate(transform, payloadJson);

        public string Evaluate(TransformSpec transform, string payloadJson, JsonElement? context)
            => Evaluate(transform, payloadJson);

        private static string Evaluate(TransformSpec transform, string payloadJson)
        {
            if (transform.Expression.Contains("$error", StringComparison.Ordinal))
                throw new TransformEvaluationException("evaluation failed");
            return payloadJson;
        }
    }
}
