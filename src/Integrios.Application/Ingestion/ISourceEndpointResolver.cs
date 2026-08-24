using System.Text.Json;
using Integrios.Domain.Entities;
using Integrios.Domain.ValueObjects;

namespace Integrios.Application.Ingestion;

public interface ISourceEndpointResolver
{
    Task<ResolvedSourceEndpoint?> ResolveAsync(
        string connectorKey,
        Guid endpointId,
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
    public required SourceVerification SourceVerification { get; init; }
}
