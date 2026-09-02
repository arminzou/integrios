namespace Integrios.Application.Authoring.Subscriptions;

public sealed record SubscriptionListDto(IReadOnlyList<SubscriptionListItemDto> Items, string? NextCursor);
