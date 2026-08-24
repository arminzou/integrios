using System.Text.Json;
using Integrios.Domain.Enums;

namespace Integrios.Domain.Entities;

public sealed record Source
{
    public required Guid Id { get; init; }
    public required Guid TenantId { get; init; }
    public required Guid ConnectionId { get; init; }
    public required Guid TopicId { get; init; }
    public required SourceType Type { get; init; }
    public required JsonElement Configuration { get; init; }
    public required SourceStatus Status { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
    public DateTimeOffset? RevokedAt { get; init; }
}
