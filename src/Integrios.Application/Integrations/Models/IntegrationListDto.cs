namespace Integrios.Application.Integrations;

public sealed record IntegrationListDto
{
    public required IReadOnlyList<IntegrationDto> Items { get; init; }
    public string? NextCursor { get; init; }
}
