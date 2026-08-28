namespace Integrios.Application.Authoring.Subscriptions;

public sealed class SubscriptionValidationException(string message, string field = "")
    : AuthoringValidationException(message, field);
