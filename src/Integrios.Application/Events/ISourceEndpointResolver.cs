using System.Text.Json;
using Integrios.Domain.Entities;
using Integrios.Domain.ValueObjects;

namespace Integrios.Application.Events;

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
    public required string SourceAdapterKey { get; init; }
    public required int SourceAdapterContractVersion { get; init; }
    public required JsonElement SourceAdapterConfig { get; init; }
    public required ConnectionSchemeSelection SourceVerification { get; init; }
}
