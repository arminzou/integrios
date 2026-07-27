using System.Diagnostics;
using Integrios.Application.Abstractions;
using Integrios.Application.Telemetry;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Integrios.Application.Events;

public sealed record IngestEventCommand(Guid TenantId, IngestEventRequest Request)
    : IRequest<IngestEventResponse>;

internal sealed class IngestEventCommandHandler(
    IEventRepository eventRepository,
    ITopicRepository topicRepository,
    IntegriosMetrics metrics,
    ILogger<IngestEventCommandHandler> logger)
    : IRequestHandler<IngestEventCommand, IngestEventResponse>
{
    public async Task<IngestEventResponse> Handle(IngestEventCommand command, CancellationToken cancellationToken)
    {
        var topicId = await topicRepository.FindActiveSourceTopicAsync(
                command.TenantId,
                command.Request.TopicName,
                command.Request.SourceConnectionId,
                cancellationToken)
            ?? throw new EventAcceptanceException(
                "The source connection must be active, source-capable, belong to this tenant, and be associated with the selected topic.");

        // The ambient request span is the acceptance span; its id becomes the trace anchor
        // carried across the outbox hop.
        var activity = Activity.Current;
        activity?.SetTag("tenant_id", command.TenantId);
        activity?.SetTag("topic_id", topicId);
        activity?.SetTag("source_connection_id", command.Request.SourceConnectionId);
        activity?.SetTag("idempotency_key", command.Request.IdempotencyKey);

        var response = await eventRepository.IngestAsync(
            command.TenantId, command.Request, topicId, activity?.Id, cancellationToken);

        activity?.SetTag("event_id", response.EventId);

        using var scope = logger.BeginScope(new Dictionary<string, object>
        {
            ["event_id"] = response.EventId,
            ["tenant_id"] = command.TenantId,
            ["topic_id"] = topicId,
            ["source_connection_id"] = command.Request.SourceConnectionId
        });

        if (!response.IsDuplicate)
        {
            metrics.RecordEventIngested();
            logger.LogInformation("Accepted event {EventId} on topic {TopicId}.", response.EventId, topicId);
        }

        return response;
    }
}
