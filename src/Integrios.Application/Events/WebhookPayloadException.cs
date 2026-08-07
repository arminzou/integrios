namespace Integrios.Application.Events;

public sealed class WebhookPayloadException(string message) : Exception(message);
