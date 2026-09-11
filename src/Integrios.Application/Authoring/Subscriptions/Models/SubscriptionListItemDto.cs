namespace Integrios.Application.Authoring.Subscriptions;

public sealed record SubscriptionListItemDto(
    Guid Id,
    Guid TopicId,
    Guid TenantId,
    string Name,
    Guid DestinationId,
    string DestinationName,
    string Status,
    int OrderIndex,
    string? Description,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
