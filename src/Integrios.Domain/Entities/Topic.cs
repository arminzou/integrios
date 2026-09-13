using Integrios.Domain.Enums;

namespace Integrios.Domain.Entities;

public sealed record Topic
{
    public required Guid Id { get; init; }
    public required Guid TenantId { get; init; }
    /// The machine identifier. Immutable, because it travels with delivered Events as `topic_name`.
    public required string Key { get; init; }
    /// The human label, which an Operator may correct at any time.
    public required string Name { get; init; }
    public required OperationalStatus Status { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
    public string? Description { get; init; }
}
