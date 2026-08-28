using System.Diagnostics;
using System.Text.Json;
using Integrios.Application.Telemetry;
using Integrios.Application.Transforms;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Integrios.Application.Ingestion;

public sealed record IngestEventCommand(
    Guid TenantId,
    Guid SourceId,
    JsonElement RawInput)
    : IRequest<IngestEventResult>;

internal sealed class IngestEventCommandHandler(
    IEventApiSourceResolver sourceResolver,
    ITransformEvaluator evaluator,
    IEventAcceptance eventAcceptance,
    IntegriosMetrics metrics,
    ILogger<IngestEventCommandHandler> logger)
    : IRequestHandler<IngestEventCommand, IngestEventResult>
{
    public async Task<IngestEventResult> Handle(IngestEventCommand command, CancellationToken cancellationToken)
    {
        ResolvedEventApiSource source = await sourceResolver.ResolveAsync(
                command.TenantId, command.SourceId, cancellationToken)
            ?? throw new SourceEndpointNotFoundException(
                "No active event_api Source matches the requested id.");

        SourceContractOutput output = SourceContractEvaluator.Evaluate(
            evaluator, source.SourceContractSchema, source.SourceMapping, command.RawInput);
        string? idempotencyKey = output.SourceEventId is { } sourceEventId
            ? $"{command.SourceId}:{sourceEventId}"
            : null;

        // The ambient request span is the acceptance span; its id becomes the trace anchor
        // carried across the outbox hop.
        var activity = Activity.Current;
        activity?.SetTag("integrios.tenant.id", command.TenantId);
        activity?.SetTag("integrios.topic.id", source.TopicId);
        activity?.SetTag("integrios.source.id", command.SourceId);

        var accepted = await eventAcceptance.AcceptAsync(
            new EventSubmission
            {
                TenantId = command.TenantId,
                TopicId = source.TopicId,
                SourceId = command.SourceId,
                SourceEventId = output.SourceEventId,
                EventType = output.EventType,
                Payload = output.Payload,
                Metadata = output.Metadata,
                IdempotencyKey = idempotencyKey
            },
            activity?.Id,
            cancellationToken);

        activity?.SetTag("integrios.event.id", accepted.EventId);

        using var scope = logger.BeginScope(new Dictionary<string, object>
        {
            ["event_id"] = accepted.EventId,
            ["tenant_id"] = command.TenantId,
            ["topic_id"] = source.TopicId,
            ["source_id"] = command.SourceId
        });

        if (!accepted.AlreadyAccepted)
        {
            metrics.RecordEventIngested();
            logger.LogInformation("Accepted event {EventId} on topic {TopicId}.", accepted.EventId, source.TopicId);
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
