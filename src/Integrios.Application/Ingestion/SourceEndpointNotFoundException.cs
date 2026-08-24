namespace Integrios.Application.Ingestion;

public sealed class SourceEndpointNotFoundException(string message) : Exception(message);
