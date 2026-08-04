using Integrios.Domain.Topics;

namespace Integrios.Application.Topics;

public sealed record TopicDto(
    Guid Id,
    Guid TenantId,
    string Name,
    IReadOnlyList<TopicSourceDto> Sources,
    string Status,
    string? Description,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public static TopicDto From(Topic t) => new(
        t.Id,
        t.TenantId,
        t.Name,
        t.Sources.Select(TopicSourceDto.From).ToList(),
        t.Status.ToString().ToLowerInvariant(),
        t.Description,
        t.CreatedAt,
        t.UpdatedAt);
}
