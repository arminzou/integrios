using System.Text.Json;
using Integrios.Application.Transforms;

namespace Integrios.Application.Ingestion;

public interface IEventApiSourceResolver
{
    Task<ResolvedEventApiSource?> ResolveAsync(
        Guid tenantId,
        Guid sourceId,
        CancellationToken cancellationToken);
}

public sealed record ResolvedEventApiSource
{
    public required Guid TopicId { get; init; }
    public required JsonElement? SourceContractSchema { get; init; }
    public required TransformSpec? SourceMapping { get; init; }
}
