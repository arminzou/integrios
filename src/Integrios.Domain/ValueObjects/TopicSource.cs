using Integrios.Domain.Enums;

namespace Integrios.Domain.ValueObjects;

public sealed record TopicSource
{
    public required Guid TenantId { get; init; }
    public required Guid TopicId { get; init; }
    public required Guid ConnectionId { get; init; }
    public required TopicSourceStatus Status { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? InactiveAt { get; init; }
    public SourceEndpoint? Endpoint { get; init; }
}
