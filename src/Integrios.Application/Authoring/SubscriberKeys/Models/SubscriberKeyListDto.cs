namespace Integrios.Application.Authoring.SubscriberKeys;

public sealed record SubscriberKeyListDto
{
    public required IReadOnlyList<SubscriberKeyListItemDto> Items { get; init; }
    public string? NextCursor { get; init; }
}
