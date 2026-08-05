namespace Integrios.Application.Tenants;

public sealed record TenantListDto
{
    public required IReadOnlyList<TenantDto> Items { get; init; }
    public string? NextCursor { get; init; }
}
