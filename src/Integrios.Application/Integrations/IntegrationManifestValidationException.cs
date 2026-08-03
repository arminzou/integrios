namespace Integrios.Application.Integrations;

public sealed class IntegrationManifestValidationException(string message) : Exception(message);

public sealed class IntegrationVersionConflictException(string message) : Exception(message);
