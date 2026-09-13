namespace Integrios.Application.Authoring.Destinations;

public sealed record DestinationListDto
{
    public required IReadOnlyList<DestinationListItemDto> Items { get; init; }
    public string? NextCursor { get; init; }
}
