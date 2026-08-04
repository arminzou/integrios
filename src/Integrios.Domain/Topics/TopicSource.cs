namespace Integrios.Domain.Topics;

public sealed record TopicSource
{
    public required Guid ConnectionId { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public SourceEndpoint? Endpoint { get; init; }
}
