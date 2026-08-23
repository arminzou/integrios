namespace Integrios.Application.Connectors;

public sealed record ConnectorListDto
{
    public required IReadOnlyList<ConnectorDto> Items { get; init; }
    public string? NextCursor { get; init; }
}
