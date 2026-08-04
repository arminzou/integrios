namespace Integrios.Application.Topics;

public sealed record TopicListDto(IReadOnlyList<TopicDto> Items, string? NextCursor);
