namespace Integrios.Domain.Topics;

public enum TopicSourceStatus
{
    Active = 0,
    Inactive = 1,
}

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
