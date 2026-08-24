namespace Integrios.Application.Authoring.Subscriptions;

public sealed record SubscriptionListDto(IReadOnlyList<SubscriptionDto> Items, string? NextCursor);
