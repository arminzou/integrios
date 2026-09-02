using Integrios.Domain.Entities;
using Integrios.Domain.Enums;

namespace Integrios.Application.Authoring.Sources;

public sealed record SourceListItemDto(
    Guid Id,
    Guid TenantId,
    Guid ConnectionId,
    Guid TopicId,
    string Type,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? RevokedAt)
{
    public static SourceListItemDto From(Source source) => new(
        source.Id,
        source.TenantId,
        source.ConnectionId,
        source.TopicId,
        source.Type == SourceType.EventApi ? "event_api" : source.Type.ToString().ToLowerInvariant(),
        source.Status.ToString().ToLowerInvariant(),
        source.CreatedAt,
        source.UpdatedAt,
        source.RevokedAt);
}
