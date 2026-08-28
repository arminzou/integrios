namespace Integrios.Application.Authoring.Connections;

public sealed class ConnectionValidationException(string message, string field = "")
    : AuthoringValidationException(message, field);
