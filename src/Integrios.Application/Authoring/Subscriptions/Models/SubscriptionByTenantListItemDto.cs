namespace Integrios.Application.Authoring.Subscriptions;

public sealed record SubscriptionByTenantListItemDto(
    Guid Id,
    Guid TopicId,
    string TopicName,
    Guid TenantId,
    string Name,
    Guid DestinationId,
    string DestinationName,
    string Status,
    int OrderIndex,
    string? Description,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
