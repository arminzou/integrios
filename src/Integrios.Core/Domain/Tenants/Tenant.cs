using Integrios.Core.Domain.Common;

namespace Integrios.Core.Domain.Tenants;

public sealed record Tenant
{
    public required Guid Id { get; init; }
    public required string Slug { get; init; }
    public required string Name { get; init; }
    public required OperationalStatus Status { get; init; }
    public string? Environment { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
    public string? Description { get; init; }
}
