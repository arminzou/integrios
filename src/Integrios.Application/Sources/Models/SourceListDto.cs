namespace Integrios.Application.Sources;

public sealed record SourceListDto(IReadOnlyList<SourceDto> Items, string? NextCursor);
