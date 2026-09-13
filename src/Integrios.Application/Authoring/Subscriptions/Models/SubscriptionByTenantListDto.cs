namespace Integrios.Application.Authoring.Subscriptions;

public sealed record SubscriptionByTenantListDto(
    IReadOnlyList<SubscriptionByTenantListItemDto> Items,
    string? NextCursor);
