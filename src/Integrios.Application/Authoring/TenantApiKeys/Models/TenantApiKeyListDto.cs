namespace Integrios.Application.Authoring.TenantApiKeys;

public sealed record TenantApiKeyListDto
{
    public required IReadOnlyList<TenantApiKeyDto> Items { get; init; }
    public string? NextCursor { get; init; }
}
