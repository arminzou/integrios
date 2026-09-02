namespace Integrios.Application.Authoring.Sources;

public sealed record SourceListDto(IReadOnlyList<SourceListItemDto> Items, string? NextCursor);
