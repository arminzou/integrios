using Integrios.Domain.Common;

namespace Integrios.Domain.Topics;

public sealed record Topic
{
    public required Guid Id { get; init; }
    public required Guid TenantId { get; init; }
    public required string Name { get; init; }
    public required IReadOnlyList<Guid> SourceConnectionIds { get; init; }
    public IReadOnlyList<SourceEndpoint> SourceEndpoints { get; init; } = [];
    public required OperationalStatus Status { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
    public string? Description { get; init; }
}

public sealed record SourceEndpoint
{
    public required Guid Id { get; init; }
    public required Guid TenantId { get; init; }
    public required Guid TopicId { get; init; }
    public required Guid ConnectionId { get; init; }
    public required string CallbackPath { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
}
