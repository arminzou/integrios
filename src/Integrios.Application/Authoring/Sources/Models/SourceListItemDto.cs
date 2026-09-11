namespace Integrios.Application.Authoring.Sources;

public sealed record SourceListItemDto(
    Guid Id,
    Guid TenantId,
    Guid ConnectorId,
    Guid TopicId,
    string Type,
    string Status,
    string InputRequirements,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? RevokedAt);
