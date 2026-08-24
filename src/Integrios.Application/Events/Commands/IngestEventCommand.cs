using System.Diagnostics;
using System.Text.Json;
using Integrios.Application.Telemetry;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Integrios.Application.Events;

public sealed record IngestEventCommand(
    Guid TenantId,
    Guid SourceId,
    string TopicName,
    string? SourceEventId,
    string EventType,
    JsonElement Payload,
    JsonElement? Metadata,
    string? IdempotencyKey)
    : IRequest<IngestEventResult>;

internal sealed class IngestEventCommandHandler(
    IEventAcceptance eventAcceptance,
    ISourceTopicLookup topicResolver,
    IntegriosMetrics metrics,
    ILogger<IngestEventCommandHandler> logger)
    : IRequestHandler<IngestEventCommand, IngestEventResult>
{
    public async Task<IngestEventResult> Handle(IngestEventCommand command, CancellationToken cancellationToken)
    {
        var topicId = await topicResolver.FindActiveSourceTopicAsync(
                command.TenantId,
                command.TopicName,
                command.SourceId,
                cancellationToken)
            ?? throw new EventAcceptanceException(
                "The source connection must be active, source-capable, belong to this tenant, and be associated with the selected topic.");

        // The ambient request span is the acceptance span; its id becomes the trace anchor
        // carried across the outbox hop.
        var activity = Activity.Current;
        activity?.SetTag("tenant_id", command.TenantId);
        activity?.SetTag("topic_id", topicId);
        activity?.SetTag("source_id", command.SourceId);
        activity?.SetTag("idempotency_key", command.IdempotencyKey);

        var accepted = await eventAcceptance.AcceptAsync(
            new EventSubmission
            {
                TenantId = command.TenantId,
                TopicId = topicId,
                SourceId = command.SourceId,
                SourceEventId = command.SourceEventId,
                EventType = command.EventType,
                Payload = command.Payload,
                Metadata = command.Metadata,
                IdempotencyKey = command.IdempotencyKey
            },
            activity?.Id,
            cancellationToken);

        activity?.SetTag("event_id", accepted.EventId);

        using var scope = logger.BeginScope(new Dictionary<string, object>
        {
            ["event_id"] = accepted.EventId,
            ["tenant_id"] = command.TenantId,
            ["topic_id"] = topicId,
            ["source_id"] = command.SourceId
        });

        if (!accepted.AlreadyAccepted)
        {
            metrics.RecordEventIngested();
            logger.LogInformation("Accepted event {EventId} on topic {TopicId}.", accepted.EventId, topicId);
        }

        return new IngestEventResult
        {
            EventId = accepted.EventId,
            Status = accepted.Status,
            AcceptedAt = accepted.AcceptedAt,
            AlreadyAccepted = accepted.AlreadyAccepted
        };
    }
}
