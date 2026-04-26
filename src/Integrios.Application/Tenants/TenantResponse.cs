using Integrios.Domain.Tenants;

namespace Integrios.Application.Tenants;

public sealed record TenantResponse
{
    public required Guid Id { get; init; }
    public required string Slug { get; init; }
    public required string Name { get; init; }
    public required string Status { get; init; }
    public string? Environment { get; init; }
    public string? Description { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }

    public static TenantResponse From(Tenant tenant) => new()
    {
        Id = tenant.Id,
        Slug = tenant.Slug,
        Name = tenant.Name,
        Status = tenant.Status.ToString().ToLowerInvariant(),
        Environment = tenant.Environment,
        Description = tenant.Description,
        CreatedAt = tenant.CreatedAt,
        UpdatedAt = tenant.UpdatedAt,
    };
}

public sealed record TenantListResponse
{
    public required IReadOnlyList<TenantResponse> Items { get; init; }
    public string? NextCursor { get; init; }
}
