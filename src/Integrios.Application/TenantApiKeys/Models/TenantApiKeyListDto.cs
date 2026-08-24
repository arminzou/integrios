namespace Integrios.Application.TenantApiKeys;

public sealed record TenantApiKeyListDto
{
    public required IReadOnlyList<TenantApiKeyDto> Items { get; init; }
    public string? NextCursor { get; init; }
}
