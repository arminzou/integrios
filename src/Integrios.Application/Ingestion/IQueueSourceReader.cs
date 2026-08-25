using System.Text.Json;
using Integrios.Application.Transforms;

namespace Integrios.Application.Ingestion;

// The desired set of running queue processors, re-read on a reconcile interval rather than per
// message: a message is processed against the facts its processor resolved when it started.
public interface IQueueSourceReader
{
    Task<IReadOnlyList<ResolvedQueueSource>> ListActiveAzureServiceBusSourcesAsync(CancellationToken cancellationToken);
}

public sealed record ResolvedQueueSource
{
    // Opaque fingerprint of everything this record was resolved from. A processor whose Revision no
    // longer matches the reader is recycled, which is what makes an edited Source or a republished
    // Connector manifest take effect without a restart.
    public required string Revision { get; init; }

    public required Guid TenantId { get; init; }
    public required string TenantSlug { get; init; }
    public required Guid TopicId { get; init; }
    public required Guid SourceId { get; init; }
    public required string Namespace { get; init; }
    // Exactly one entity form is populated, guaranteed by SourceAuthoringValidator: a queue, or a
    // topic with the subscription consumed from it.
    public required string? QueueName { get; init; }
    // Named for the broker, not shortened: TopicId on this same record is the Integrios Topic.
    public required string? ServiceBusTopicName { get; init; }
    public required string? ServiceBusSubscriptionName { get; init; }
    public required QueueAuthentication Authentication { get; init; }
    public required JsonElement? SourceContractSchema { get; init; }
    public required TransformSpec? SourceMapping { get; init; }
}

public sealed record QueueAuthentication
{
    public required string Scheme { get; init; }
    public string? SecretReference { get; init; }
}
