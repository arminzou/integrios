namespace Integrios.Application.Authoring.Sources;

public sealed record SourceListItemDto(
    Guid Id,
    Guid TenantId,
    Guid ConnectionId,
    Guid TopicId,
    string Type,
    string Status,
    string SourceContract,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? RevokedAt);
