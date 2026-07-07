namespace Integrios.Application.Connections;

public sealed class ConnectionRequestValidationException(string message) : Exception(message);
