namespace Integrios.Application.Authoring.Connectors;

public sealed record ConnectorListDto
{
    public required IReadOnlyList<ConnectorListItemDto> Items { get; init; }
    public string? NextCursor { get; init; }
}
