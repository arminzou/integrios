namespace Integrios.Application.Authoring.Connectors;

public sealed class ConnectorManifestValidationException(string message, string field = "")
    : AuthoringValidationException(message, field);

public sealed class ConnectorVersionConflictException(string message) : Exception(message);
