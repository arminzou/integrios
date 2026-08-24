namespace Integrios.Application.Ingestion;

public sealed class EventAcceptanceException(string message) : Exception(message);
