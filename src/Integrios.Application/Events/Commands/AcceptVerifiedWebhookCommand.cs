using System.Diagnostics;
using Integrios.Application.Connectors;
using Integrios.Application.Telemetry;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Integrios.Application.Events;

public sealed record AcceptVerifiedWebhookCommand(
    string ConnectorKey,
    Guid EndpointId,
    string? ContentType,
    IReadOnlyDictionary<string, string> Headers,
    ReadOnlyMemory<byte> RawBody)
    : IRequest<IngestEventResult>;

internal sealed class AcceptVerifiedWebhookCommandHandler(
    ISourceEndpointResolver endpointResolver,
    IIngressSourceAdapterRuntime adapterRuntime,
    IEventAcceptance eventAcceptance,
    IntegriosMetrics metrics,
    ILogger<AcceptVerifiedWebhookCommandHandler> logger)
    : IRequestHandler<AcceptVerifiedWebhookCommand, IngestEventResult>
{
    public async Task<IngestEventResult> Handle(AcceptVerifiedWebhookCommand command, CancellationToken cancellationToken)
    {
        ResolvedSourceEndpoint endpoint = await endpointResolver.ResolveAsync(
                command.ConnectorKey, command.EndpointId, cancellationToken)
            ?? throw new SourceEndpointNotFoundException("No active source endpoint matches this callback URL.");

        IIngressSourceAdapter adapter = adapterRuntime.GetRequired(
            endpoint.SourceAdapterKey, endpoint.SourceAdapterContractVersion);

        EventSubmission submission = await adapter.ExecuteAsync(
            new SourceAdapterExecutionContext
            {
                TenantId = endpoint.TenantId,
                TenantSlug = endpoint.TenantSlug,
                TopicId = endpoint.TopicId,
                SourceConnectionId = endpoint.ConnectionId,
                EndpointId = command.EndpointId,
                ConnectorKey = endpoint.ConnectorKey,
                AdapterConfig = endpoint.SourceAdapterConfig,
                SourceVerification = endpoint.SourceVerification,
                ContentType = command.ContentType,
                Headers = command.Headers,
                RawBody = command.RawBody,
            },
            cancellationToken);

        var activity = Activity.Current;
        activity?.SetTag("tenant_id", endpoint.TenantId);
        activity?.SetTag("topic_id", endpoint.TopicId);
        activity?.SetTag("source_connection_id", endpoint.ConnectionId);

        var accepted = await eventAcceptance.AcceptAsync(submission, activity?.Id, cancellationToken);

        activity?.SetTag("event_id", accepted.EventId);

        using var scope = logger.BeginScope(new Dictionary<string, object>
        {
            ["event_id"] = accepted.EventId,
            ["tenant_id"] = endpoint.TenantId,
            ["topic_id"] = endpoint.TopicId,
            ["source_connection_id"] = endpoint.ConnectionId
        });

        if (!accepted.AlreadyAccepted)
        {
            metrics.RecordEventIngested();
            logger.LogInformation("Accepted webhook event {EventId} on topic {TopicId}.", accepted.EventId, endpoint.TopicId);
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
