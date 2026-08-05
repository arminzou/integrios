namespace Integrios.Application.Connections;

public sealed class ConnectionValidationException(string message) : Exception(message);
