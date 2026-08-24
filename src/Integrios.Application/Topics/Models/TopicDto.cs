using Integrios.Domain.Entities;
using Integrios.Domain.Enums;

namespace Integrios.Application.Topics;

public sealed record TopicDto(
    Guid Id,
    Guid TenantId,
    string Name,
    string Status,
    string? Description,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public static TopicDto From(Topic t) => new(
        t.Id,
        t.TenantId,
        t.Name,
        t.Status.ToString().ToLowerInvariant(),
        t.Description,
        t.CreatedAt,
        t.UpdatedAt);
}
