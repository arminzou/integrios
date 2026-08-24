using System.Text.Json;
using Integrios.Application.Transforms;

namespace Integrios.Application.Ingestion;

// Loaded once at Ingestion startup, not resolved per message: V1 queue receivers are
// startup-loaded and restart-activated, not dynamically reconciled.
public interface IQueueSourceCatalog
{
    Task<IReadOnlyList<ResolvedQueueSource>> ListActiveAzureServiceBusSourcesAsync(CancellationToken cancellationToken);
}

public sealed record ResolvedQueueSource
{
    public required Guid TenantId { get; init; }
    public required string TenantSlug { get; init; }
    public required Guid TopicId { get; init; }
    public required Guid SourceId { get; init; }
    public required string Namespace { get; init; }
    public required string QueueName { get; init; }
    public required QueueAuthentication Authentication { get; init; }
    public required JsonElement? SourceContractSchema { get; init; }
    public required TransformSpec? SourceMapping { get; init; }
}

public sealed record QueueAuthentication
{
    public required string Scheme { get; init; }
    public string? SecretReference { get; init; }
}
