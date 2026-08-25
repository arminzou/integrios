using System.Diagnostics;
using System.Text.Json;
using Integrios.Application.Telemetry;
using Integrios.Application.Transforms;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Integrios.Application.Ingestion;

// The queue receiver already resolved its Source once at startup (V1 reader is startup-loaded,
// not reconciled per message), so this command carries the resolved Source facts directly rather
// than a Source id to look up again per message.
public sealed record AcceptQueueMessageCommand(
    Guid TenantId,
    Guid TopicId,
    Guid SourceId,
    JsonElement? SourceContractSchema,
    TransformSpec? SourceMapping,
    JsonElement RawInput)
    : IRequest<IngestEventResult>;

internal sealed class AcceptQueueMessageCommandHandler(
    ITransformEvaluator evaluator,
    IEventAcceptance eventAcceptance,
    IntegriosMetrics metrics,
    ILogger<AcceptQueueMessageCommandHandler> logger)
    : IRequestHandler<AcceptQueueMessageCommand, IngestEventResult>
{
    public async Task<IngestEventResult> Handle(AcceptQueueMessageCommand command, CancellationToken cancellationToken)
    {
        SourceContractOutput output = SourceContractEvaluator.Evaluate(
            evaluator, command.SourceContractSchema, command.SourceMapping, command.RawInput);
        string? idempotencyKey = output.SourceEventId is { } sourceEventId
            ? $"service_bus:{command.SourceId}:{sourceEventId}"
            : null;

        var activity = Activity.Current;
        activity?.SetTag("tenant_id", command.TenantId);
        activity?.SetTag("topic_id", command.TopicId);
        activity?.SetTag("source_id", command.SourceId);
        var accepted = await eventAcceptance.AcceptAsync(
            new EventSubmission
            {
                TenantId = command.TenantId,
                TopicId = command.TopicId,
                SourceId = command.SourceId,
                SourceEventId = output.SourceEventId,
                EventType = output.EventType,
                Payload = output.Payload,
                Metadata = output.Metadata,
                IdempotencyKey = idempotencyKey,
            },
            activity?.Id,
            cancellationToken);
        activity?.SetTag("event_id", accepted.EventId);

        using var scope = logger.BeginScope(new Dictionary<string, object>
        {
            ["event_id"] = accepted.EventId, ["tenant_id"] = command.TenantId,
            ["topic_id"] = command.TopicId, ["source_id"] = command.SourceId
        });
        if (!accepted.AlreadyAccepted)
        {
            metrics.RecordEventIngested();
            logger.LogInformation("Accepted queue event {EventId} on topic {TopicId}.", accepted.EventId, command.TopicId);
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
