namespace Integrios.Application.Authoring.Topics;

public sealed record TopicListDto(IReadOnlyList<TopicDto> Items, string? NextCursor);
