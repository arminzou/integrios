namespace Integrios.Application.Authoring.SubscriberKeys;

public sealed class SubscriberKeyValidationException(string message, string field = "")
    : AuthoringValidationException(message, field);
