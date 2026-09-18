using Integrios.Domain.Enums;

namespace Integrios.Application.Authoring.Subscriptions;

public sealed record SubscriptionListFilter(
    EnablementStatus? Status = null,
    Guid? TopicId = null,
    Guid? DestinationId = null,
    string? NameContains = null);
