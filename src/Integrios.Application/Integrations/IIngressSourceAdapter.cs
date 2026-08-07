using System.Text.Json;
using Integrios.Application.Events;
using Integrios.Domain.Connections;

namespace Integrios.Application.Integrations;

public interface IIngressSourceAdapter
{
    string Key { get; }

    int ContractVersion { get; }

    Task<EventSubmission> ExecuteAsync(
        SourceAdapterExecutionContext context,
        CancellationToken cancellationToken);
}

public sealed record SourceAdapterExecutionContext
{
    public required Guid TenantId { get; init; }
    public required string TenantSlug { get; init; }
    public required Guid TopicId { get; init; }
    public required Guid SourceConnectionId { get; init; }
    public required Guid EndpointId { get; init; }
    public required string IntegrationKey { get; init; }
    public required JsonElement AdapterConfig { get; init; }
    public required ConnectionSchemeSelection SourceVerification { get; init; }
    public string? ContentType { get; init; }
    public required IReadOnlyDictionary<string, string> Headers { get; init; }
    public required ReadOnlyMemory<byte> RawBody { get; init; }
}
