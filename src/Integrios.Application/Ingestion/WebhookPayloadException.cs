namespace Integrios.Application.Ingestion;

public sealed class WebhookPayloadException(string message) : Exception(message);
