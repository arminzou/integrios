namespace Integrios.Application.Events;

public sealed class SourceEndpointNotFoundException(string message) : Exception(message);
