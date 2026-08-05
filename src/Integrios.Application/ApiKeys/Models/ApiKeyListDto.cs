namespace Integrios.Application.ApiKeys;

public sealed record ApiKeyListDto
{
    public required IReadOnlyList<ApiKeyDto> Items { get; init; }
    public string? NextCursor { get; init; }
}
