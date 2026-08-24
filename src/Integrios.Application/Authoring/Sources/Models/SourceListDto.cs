namespace Integrios.Application.Authoring.Sources;

public sealed record SourceListDto(IReadOnlyList<SourceDto> Items, string? NextCursor);
