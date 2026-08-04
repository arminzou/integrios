using Integrios.Domain.Topics;

namespace Integrios.Application.Topics;

public sealed record TopicSourceDto(
    Guid ConnectionId,
    DateTimeOffset CreatedAt,
    SourceEndpointDto? Endpoint)
{
    public static TopicSourceDto From(TopicSource source) => new(
        source.ConnectionId,
        source.CreatedAt,
        source.Endpoint is null ? null : SourceEndpointDto.From(source.Endpoint));
}
