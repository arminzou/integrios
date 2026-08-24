using System.Text.Json;
using Integrios.Domain.Entities;

namespace Integrios.Application.Authoring.Sources;

public sealed record SourceDto(
    Guid Id,
    Guid TenantId,
    Guid ConnectionId,
    Guid TopicId,
    string Type,
    JsonElement Configuration,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? RevokedAt)
{
    public static SourceDto From(Source source) => new(
        source.Id,
        source.TenantId,
        source.ConnectionId,
        source.TopicId,
        source.Type.ToString().ToLowerInvariant(),
        source.Configuration,
        source.Status.ToString().ToLowerInvariant(),
        source.CreatedAt,
        source.UpdatedAt,
        source.RevokedAt);
}
