using System.Text.Json;
using Integrios.Application.Transforms;
using Integrios.Domain.ValueObjects;

namespace Integrios.Application.Ingestion;

public interface ISourceEndpointResolver
{
    Task<ResolvedSourceEndpoint?> ResolveAsync(
        Guid callbackId,
        CancellationToken cancellationToken);
}

public sealed record ResolvedSourceEndpoint
{
    public required Guid TenantId { get; init; }
    public required string TenantSlug { get; init; }
    public required Guid TopicId { get; init; }
    public required Guid SourceId { get; init; }
    public required Guid ConnectionId { get; init; }
    public required string ConnectorKey { get; init; }
    public SourceVerification? SourceVerification { get; init; }
    public required JsonElement? SourceContractSchema { get; init; }
    public required TransformSpec? SourceMapping { get; init; }
}
