namespace Integrios.Application.Authoring.Sources;

public sealed class SourceValidationException(string message, string field = "")
    : AuthoringValidationException(message, field);
