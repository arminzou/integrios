namespace Integrios.Application.Subscriptions;

public sealed record SubscriptionListDto(IReadOnlyList<SubscriptionDto> Items, string? NextCursor);
