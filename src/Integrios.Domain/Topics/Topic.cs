using Integrios.Domain.Common;

namespace Integrios.Domain.Topics;

public sealed record Topic
{
    public required Guid Id { get; init; }
    public required Guid TenantId { get; init; }
    public required string Name { get; init; }
    public required Guid SourceConnectionId { get; init; }
    public required IReadOnlyList<string> EventTypes { get; init; }
    public required OperationalStatus Status { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
    public string? Description { get; init; }
}
