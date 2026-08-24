namespace Integrios.Application.Authoring.Connections;

public sealed record ConnectionListDto
{
    public required IReadOnlyList<ConnectionDto> Items { get; init; }
    public string? NextCursor { get; init; }
}
