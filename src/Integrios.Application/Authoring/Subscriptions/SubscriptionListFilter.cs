using Integrios.Domain.Enums;

namespace Integrios.Application.Authoring.Subscriptions;

public sealed record SubscriptionListFilter(
    OperationalStatus? Status = null,
    Guid? TopicId = null,
    Guid? DestinationConnectionId = null,
    string? NameContains = null);
