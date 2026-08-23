namespace Integrios.Application.Connectors;

public sealed class ConnectorManifestValidationException(string message) : Exception(message);

public sealed class ConnectorVersionConflictException(string message) : Exception(message);
